using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Momentum4.Core.Transport;

namespace Momentum4.CoreHost;

public sealed class InheritedTransport : ITransport
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task? _reader;
    private int _connected;
    private int _disposed;
    private int _state;

    public InheritedTransport(int descriptor)
    {
        if (descriptor < 3) throw new ArgumentOutOfRangeException(nameof(descriptor));
        _socket = new Socket(new SafeSocketHandle((IntPtr)descriptor, ownsHandle: true));
    }

    public TransportState State => (TransportState)Volatile.Read(ref _state);
    public event EventHandler<TransportState>? StateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _connected, 1) != 0 || Volatile.Read(ref _disposed) != 0) throw new IOException("Inherited transport is one-shot");
        SetState(TransportState.Connected);
        _reader = Task.Run(ReadAsync);
        return Task.CompletedTask;
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var count = await _socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (count == 0) break;
                DataReceived?.Invoke(this, buffer[..count]);
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (!_stop.IsCancellationRequested) SetState(TransportState.Lost);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (State != TransportState.Connected) throw new IOException("Inherited transport is unavailable");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var written = await _socket.SendAsync(data[offset..], SocketFlags.None, cancellationToken);
                if (written <= 0) throw new IOException("Inherited transport write failed");
                offset += written;
            }
        }
        catch
        {
            SetState(TransportState.Lost);
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _stop.CancelAsync();
        try { _socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        _socket.Dispose();
        if (_reader is not null) await _reader;
        SetState(TransportState.Disconnected);
    }

    private void SetState(TransportState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) != (int)state) StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisconnectAsync();
        _stop.Dispose();
        _writeGate.Dispose();
    }
}
