// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Queue;

/// <summary>
/// Zentrale Stelle für alle Requests ans Headset (docs/architecture.md §6.2):
/// <list type="bullet">
/// <item>genau ein offener Request; weitere warten der Reihe nach,</item>
/// <item>Freigabe über <see cref="CommandPolicy"/> und erwartete Payload-Länge vor dem Senden,</item>
/// <item>Zuordnung der Antwort über Vendor + Command (Response <c>+0x0100</c>, Error <c>+0x0180</c>),</item>
/// <item>Notifications (<c>+0x0080</c>) laufen getrennt über <see cref="NotificationReceived"/>,</item>
/// <item>Timeout 5 s (oder aus dem Katalog), eine Wiederholung nur für <see cref="SafetyClass.Read"/>,</item>
/// <item>bei Verbindungsverlust scheitert der offene Request sofort.</item>
/// </list>
/// </summary>
public sealed class GaiaCommandQueue : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ITransport _transport;
    private readonly CommandPolicy _policy;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly GaiaFrameReader _reader;
    private readonly Lock _readerGate = new();
    private readonly Lock _pendingGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private PendingRequest? _pending;
    private long _mutationSequence;

    public GaiaCommandQueue(ITransport transport, CommandPolicy policy, ILogger<GaiaCommandQueue> logger, TimeProvider? timeProvider = null, bool strictFrames = false)
    {
        _reader = new GaiaFrameReader(strictFrames);
        _transport = transport;
        _policy = policy;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _transport.DataReceived += OnDataReceived;
        _transport.StateChanged += OnTransportStateChanged;
    }

    /// <summary>Unaufgeforderte Meldungen des Headsets (Pakettyp Notification). Aufruf aus dem Empfangs-Thread.</summary>
    public event EventHandler<GaiaFrame>? NotificationReceived;

    public event EventHandler? ProtocolFaulted;

    /// <summary>
    /// Für Antwort-Frames ohne passenden Request. Liefert der Handler <c>true</c>, war der Frame eine bekannte
    /// Zustandsmeldung (z. B. <c>0x0502</c> nach der Anmeldung von Feature 2) und gilt nicht als Fehler.
    /// </summary>
    public Func<GaiaFrame, bool>? UnsolicitedResponseHandler { get; set; }

    /// <summary>Jeder gesendete und empfangene Frame (für Protocol Logging und Mitschnitte).</summary>
    public event EventHandler<GaiaTraffic>? Traffic;

    public CommandPolicy Policy => _policy;

    internal long MutationSequence => Interlocked.Read(ref _mutationSequence);

    internal bool IsConnected => _transport.State == TransportState.Connected;

    public Task<GaiaFrame> SendAsync(CommandDescriptor command, CancellationToken cancellationToken) =>
        SendAsync(command, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public async Task<GaiaFrame> SendAsync(CommandDescriptor command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_policy.IsAllowed(command, out var reason))
        {
            _logger.LogWarning("Nicht gesendet: {Command} – {Reason}.", command, reason);
            throw new CommandNotAllowedException(command, reason);
        }

        if (command.RequestLength is { } expected && payload.Length != expected)
        {
            throw new ArgumentException($"{command}: Payload muss {expected} Byte lang sein, nicht {payload.Length}.", nameof(payload));
        }

        var attempts = command.Safety == SafetyClass.Read ? 2 : 1;
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await SendOnceAsync(command, payload, cancellationToken);
                }
                catch (TimeoutException) when (attempt < attempts)
                {
                    _logger.LogWarning("{Command}: keine Antwort, Wiederholung {Attempt}/{Max}.", command, attempt + 1, attempts);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        _transport.DataReceived -= OnDataReceived;
        _transport.StateChanged -= OnTransportStateChanged;
        FailPending(new ObjectDisposedException(nameof(GaiaCommandQueue)));
        _sendGate.Dispose();
    }

    private async Task<GaiaFrame> SendOnceAsync(CommandDescriptor command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_transport.State != TransportState.Connected)
        {
            throw new HeadsetUnavailableException($"{command}: Transport ist nicht verbunden ({_transport.State}).");
        }

        var frame = GaiaFrameCodec.Encode(command.Vendor, command.Command, payload.Span);
        var pending = new PendingRequest(command, _time.GetTimestamp());
        lock (_pendingGate)
        {
            _pending = pending;
        }

        try
        {
            _logger.LogInformation("TX {Vendor:X4}:{Command:X4} {Name} [{Payload}]", command.Vendor, command.Command.Value, command.Name, Hex.Format(payload.Span));
            RaiseTraffic(new GaiaTraffic(_time.GetUtcNow(), TrafficDirection.Tx, frame, command.Name, null));
            if (command.Safety is SafetyClass.Setting or SafetyClass.Action)
            {
                Interlocked.Increment(ref _mutationSequence);
            }

            try
            {
                await _transport.WriteAsync(frame, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not HeadsetUnavailableException)
            {
                throw new HeadsetUnavailableException("Headset transport I/O failed.", exception);
            }

            var timeout = command.Timeout ?? DefaultTimeout;
            GaiaFrame response;
            try
            {
                response = await pending.Completion.Task.WaitAsync(timeout, _time, cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("{Command}: keine Antwort nach {Timeout:0.0} s.", command, timeout.TotalSeconds);
                throw new TimeoutException($"{command}: keine Antwort nach {timeout.TotalSeconds:0.0} s.");
            }

            var duration = _time.GetElapsedTime(pending.StartTimestamp);
            if (response.Type == PacketType.Error)
            {
                if (response.Payload.Length != 1)
                {
                    throw new ProtocolFormatException("Malformed GAIA error response", response.Payload);
                }

                var error = new GaiaErrorException(command, response);
                _logger.LogWarning("RX {Vendor:X4}:{Command:X4} {Name} [{Payload}] {Ms} ms ABGELEHNT", response.Vendor, response.Command.Value, command.Name, Hex.Format(response.Payload), (int)duration.TotalMilliseconds);
                throw error;
            }

            if (command.Safety is SafetyClass.Setting or SafetyClass.Action && command != CommandCatalog.RegisterNotification && response.Payload.Length != 0)
            {
                throw new ProtocolFormatException("Malformed setter acknowledgement", response.Payload);
            }

            _logger.LogInformation("RX {Vendor:X4}:{Command:X4} {Name} [{Payload}] {Ms} ms OK", response.Vendor, response.Command.Value, command.Name, Hex.Format(response.Payload), (int)duration.TotalMilliseconds);
            return response;
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    private void OnDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        GaiaReadResult result;
        try
        {
            lock (_readerGate)
            {
                result = _reader.Push(data.Span);
            }
        }
        catch (ProtocolFormatException exception)
        {
            FailPending(exception);
            ProtocolFaulted?.Invoke(this, EventArgs.Empty);
            return;
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            _logger.LogWarning("Empfang: {Kind} – {Message} [{Bytes}]", diagnostic.Kind, diagnostic.Message, Hex.Format(diagnostic.Bytes));
        }

        foreach (var frame in result.Frames)
        {
            Dispatch(frame);
        }
    }

    private void Dispatch(GaiaFrame frame)
    {
        var known = CommandCatalog.Find(frame.Vendor, frame.Command);
        PendingRequest? pending;
        lock (_pendingGate)
        {
            pending = _pending;
        }

        TimeSpan? duration = pending is not null && IsAnswerTo(frame, pending.Command) ? _time.GetElapsedTime(pending.StartTimestamp) : null;
        RaiseTraffic(new GaiaTraffic(_time.GetUtcNow(), TrafficDirection.Rx, frame.Raw, known?.Name, duration));

        switch (frame.Type)
        {
            case PacketType.Notification:
                _logger.LogInformation("RX {Vendor:X4}:{Command:X4} Notification {Name} [{Payload}]", frame.Vendor, frame.Command.Value, known?.Name ?? "?", Hex.Format(frame.Payload));
                try
                {
                    NotificationReceived?.Invoke(this, frame);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fehler im NotificationReceived-Handler.");
                }

                return;

            case PacketType.Command:
                _logger.LogWarning("Headset sendet einen Command-Frame {Frame} – ignoriert.", frame);
                return;
        }

        if (pending is not null && IsAnswerTo(frame, pending.Command))
        {
            pending.Completion.TrySetResult(frame);
            return;
        }

        if (frame.Type == PacketType.Response && UnsolicitedResponseHandler is { } handler && TryHandle(handler, frame))
        {
            _logger.LogInformation("RX {Vendor:X4}:{Command:X4} Antwort ohne Request {Name} [{Payload}] – als Zustandsmeldung übernommen.", frame.Vendor, frame.Command.Value, known?.Name ?? "?", Hex.Format(frame.Payload));
            return;
        }

        _logger.LogWarning("Unerwartete Antwort {Frame} (verspätet oder ohne passenden Request) – ignoriert.", frame);
    }

    private bool TryHandle(Func<GaiaFrame, bool> handler, GaiaFrame frame)
    {
        try
        {
            return handler(frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler im UnsolicitedResponseHandler.");
            return false;
        }
    }

    private static bool IsAnswerTo(GaiaFrame frame, CommandDescriptor command) =>
        frame.Type is PacketType.Response or PacketType.Error
        && frame.Vendor == command.Vendor
        && frame.Command.AsCommand() == command.Command;

    private void OnTransportStateChanged(object? sender, TransportState state)
    {
        switch (state)
        {
            case TransportState.Connected:
                lock (_readerGate)
                {
                    _reader.Reset();
                }

                break;
            case TransportState.Lost or TransportState.Disconnected:
                FailPending(new HeadsetUnavailableException($"Verbindung zum Headset getrennt ({state})."));
                break;
        }
    }

    private void FailPending(Exception exception)
    {
        PendingRequest? pending;
        lock (_pendingGate)
        {
            pending = _pending;
        }

        pending?.Completion.TrySetException(exception);
    }

    private void RaiseTraffic(GaiaTraffic traffic)
    {
        try
        {
            Traffic?.Invoke(this, traffic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler im Traffic-Handler.");
        }
    }

    private sealed class PendingRequest(CommandDescriptor command, long startTimestamp)
    {
        public CommandDescriptor Command { get; } = command;

        public long StartTimestamp { get; } = startTimestamp;

        public TaskCompletionSource<GaiaFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
