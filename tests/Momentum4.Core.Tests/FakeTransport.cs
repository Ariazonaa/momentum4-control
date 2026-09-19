// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using Momentum4.Core.Transport;

namespace Momentum4.Core.Tests;

/// <summary>Transport ohne Hardware: zeichnet Writes auf und liefert vorgegebene Antworten.</summary>
internal sealed class FakeTransport : ITransport
{
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>();
    private readonly Lock _gate = new();

    public event EventHandler<TransportState>? StateChanged;

    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public TransportState State { get; private set; } = TransportState.Connected;

    /// <summary>Wird bei jedem Write aufgerufen; die gelieferten Blöcke kommen sofort als Empfangsdaten zurück.</summary>
    public Func<byte[], IEnumerable<byte[]>>? Responder { get; set; }

    public List<byte[]> Writes { get; } = [];

    /// <summary>So viele der nächsten Verbindungsversuche schlagen fehl.</summary>
    public int ConnectFailures { get; set; }

    public int ConnectCount { get; private set; }

    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return Writes.Count;
            }
        }
    }

    public ValueTask<byte[]> NextWriteAsync(CancellationToken cancellationToken = default) => _writes.Reader.ReadAsync(cancellationToken);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        if (ConnectFailures > 0)
        {
            ConnectFailures--;
            throw new HeadsetUnavailableException("simuliert: Headset nicht erreichbar");
        }

        SetState(TransportState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (State != TransportState.Disconnected)
        {
            SetState(TransportState.Disconnected);
        }

        return Task.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (State != TransportState.Connected)
        {
            throw new InvalidOperationException("nicht verbunden");
        }

        var bytes = data.ToArray();
        lock (_gate)
        {
            Writes.Add(bytes);
        }

        _writes.Writer.TryWrite(bytes);
        foreach (var chunk in Responder?.Invoke(bytes) ?? [])
        {
            Receive(chunk);
        }

        return ValueTask.CompletedTask;
    }

    public void Receive(byte[] bytes) => DataReceived?.Invoke(this, bytes);

    public void SetState(TransportState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
