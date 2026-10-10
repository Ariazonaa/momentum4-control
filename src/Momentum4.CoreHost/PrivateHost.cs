using System.Text.Json;

namespace Momentum4.CoreHost;

public sealed record HostValue(Action<Utf8JsonWriter> Write);

public sealed class PrivateHost(Stream input, Stream output) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly PrivateLineReader _reader = new(input);
    private long _osCount;
    private long _progressCount;
    private long _lastRequest;
    private long _activeRequest;
    private long _nextOs;
    private long _awaitedOs;
    private TaskCompletionSource<JsonElement>? _osReply;
    private PrivateRequest? _pendingRequest;
    private bool _responding;
    private Task? _operation;
    private Exception? _readFailure;

    public CancellationToken Lifetime => _lifetime.Token;

    public async Task RunAsync(Func<PrivateRequest, CancellationToken, Task<HostValue>> dispatch, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(_lifetime.Cancel);
        var reader = ReadAsync(dispatch);
        try { await Task.WhenAny(reader, Task.Delay(Timeout.Infinite, _lifetime.Token)); }
        finally
        {
            await _lifetime.CancelAsync();
            Task? operation;
            lock (_gate) operation = _operation;
            if (operation is not null)
            {
                try { await operation; }
                catch (OperationCanceledException) { }
            }
        }
        if (reader.IsCompleted || Volatile.Read(ref _readFailure) is not null) await reader;
    }

    private async Task ReadAsync(Func<PrivateRequest, CancellationToken, Task<HostValue>> dispatch)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _reader.ReadAsync(_lifetime.Token);
                if (line is null) break;
                using var document = PrivateProtocol.Parse(line);
                var value = document.RootElement;
                var kind = PrivateProtocol.Text(value, "kind", 16);
                if (kind is "os-result" or "os-error")
                {
                    CompleteOs(value, kind);
                    continue;
                }
                var request = PrivateProtocol.Request(value);
                lock (_gate)
                {
                    if (request.Id != _lastRequest + 1) throw new PrivateProtocolException();
                    if (_activeRequest != 0)
                    {
                        if (!_responding || _pendingRequest is not null) throw new PrivateProtocolException();
                        _lastRequest = request.Id;
                        _pendingRequest = request;
                    }
                    else BeginRequest(request, dispatch);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Volatile.Write(ref _readFailure, error);
            throw;
        }
        finally { await _lifetime.CancelAsync(); }
    }

    private void BeginRequest(PrivateRequest request, Func<PrivateRequest, CancellationToken, Task<HostValue>> dispatch)
    {
        _osCount = 0;
        _progressCount = 0;
        _lastRequest = request.Id;
        _activeRequest = request.Id;
        _operation = Task.Run(() => ExecuteAsync(request, dispatch));
    }

    private void CompleteOs(JsonElement value, string kind)
    {
        PrivateProtocol.Fields(value, kind == "os-result" ? ["v", "kind", "id", "request", "value"] : ["v", "kind", "id", "request", "code"]);
        if (PrivateProtocol.Integer(value, "v") != 1) throw new PrivateProtocolException();
        TaskCompletionSource<JsonElement> reply;
        lock (_gate)
        {
            if (_osReply is null || PrivateProtocol.Integer(value, "id") != _awaitedOs || PrivateProtocol.Integer(value, "request") != _activeRequest) throw new PrivateProtocolException();
            reply = _osReply;
            _osReply = null;
            _awaitedOs = 0;
        }
        if (kind == "os-error")
        {
            if (PrivateProtocol.Text(value, "code", 16) is not ("io" or "removed")) throw new PrivateProtocolException();
            reply.TrySetException(new HostOsException(PrivateProtocol.Text(value, "code", 16) == "removed"));
        }
        else reply.TrySetResult(value.GetProperty("value").Clone());
    }

    private async Task ExecuteAsync(PrivateRequest request, Func<PrivateRequest, CancellationToken, Task<HostValue>> dispatch)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(WatchdogFor(request.Operation));
        try
        {
            var result = await dispatch(request, budget.Token);
            lock (_gate) _responding = true;
            await SendAsync(writer =>
            {
                Header(writer, "result", request.Id);
                writer.WritePropertyName("value");
                result.Write(writer);
                writer.WriteEndObject();
            }, budget.Token);
            lock (_gate)
            {
                _activeRequest = 0;
                _responding = false;
                if (_pendingRequest is { } pending)
                {
                    _pendingRequest = null;
                    BeginRequest(pending, dispatch);
                }
            }
        }
        catch (Exception error)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                var failure = HostFailure.From(error);
                using var errorBudget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                errorBudget.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    await SendAsync(writer =>
                    {
                        Header(writer, "error", request.Id);
                        writer.WriteString("code", failure.Code);
                        writer.WriteString("stage", failure.Stage);
                        writer.WriteBoolean("recovery_required", failure.RecoveryRequired);
                        writer.WriteBoolean("interrupted", failure.Interrupted);
                        writer.WriteEndObject();
                    }, errorBudget.Token);
                }
                catch (Exception outputError) when (outputError is IOException or OperationCanceledException) { }
            }
            await _lifetime.CancelAsync();
        }
    }

    public static TimeSpan WatchdogFor(string operation) => (operation switch
    {
        "dongle-status" => Momentum4.Core.Dongle.DongleService.StatusTimeout,
        "dongle-codec" => Momentum4.Core.Dongle.DongleService.CodecTimeout,
        "dongle-check" => Momentum4.Core.Dongle.DongleFirmwareService.PreparationTimeout,
        "dongle-apply" => Momentum4.Core.Dongle.DongleFirmwareService.UpdateTimeout,
        _ => Momentum4.Core.Services.Momentum4Service.ControlOperationTimeout,
    }) + TimeSpan.FromSeconds(1);

    public async Task<JsonElement> OsCallAsync(string operation, Action<Utf8JsonWriter> arguments, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonElement> reply;
        long id;
        long request;
        lock (_gate)
        {
            if (_activeRequest == 0 || _osReply is not null || ++_osCount > 102000) throw new PrivateProtocolException();
            id = ++_nextOs;
            request = _activeRequest;
            _awaitedOs = id;
            _osReply = reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await SendAsync(writer =>
        {
            Header(writer, "os-call", id);
            writer.WriteNumber("request", request);
            writer.WriteString("op", operation);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            arguments(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken);
        return await reply.Task.WaitAsync(cancellationToken);
    }

    public Task ProgressAsync(string stage, int percent, CancellationToken cancellationToken) => SendAsync(writer =>
    {
        long request;
        lock (_gate)
        {
            if (_activeRequest == 0 || ++_progressCount > 1024) throw new PrivateProtocolException();
            request = _activeRequest;
        }
        Header(writer, "progress", request);
        writer.WriteString("stage", stage);
        writer.WriteNumber("percent", percent);
        writer.WriteEndObject();
    }, cancellationToken);

    private async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var buffer = new BoundedReplyStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        if (buffer.Length > PrivateProtocol.ReplyLimit) throw new PrivateProtocolException();
        await _outputGate.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), cancellationToken);
            await output.WriteAsync(new byte[] { 10 }, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally { _outputGate.Release(); }
    }

    private static void Header(Utf8JsonWriter writer, string kind, long id)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", 1);
        writer.WriteString("kind", kind);
        writer.WriteNumber("id", id);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        _outputGate.Dispose();
    }
}

public sealed class HostOsException(bool removed) : IOException
{
    public bool Removed { get; } = removed;
}

public sealed record HostFailure(string Code, string Stage = "", bool RecoveryRequired = false, bool Interrupted = false)
{
    public static HostFailure From(Exception error) => error switch
    {
        OperationCanceledException => new("cancelled"),
        PrivateProtocolException or ArgumentException => new("invalid"),
        Momentum4.Core.Dongle.DongleUpdateException update => new("firmware", update.Stage, update.RecoveryRequired, update.Interrupted),
        Momentum4.Core.Services.HeadsetPartialWriteException => new("partial"),
        Momentum4.Core.Services.FeatureNotSupportedException => new("unsupported"),
        _ => new("io")
    };
}
