// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Transport;

/// <summary>
/// Byte-Kanal zum Headset. Kennt kein GAIA – Framing und Commands liegen darüber
/// (docs/architecture.md §6.1).
/// </summary>
public interface ITransport : IAsyncDisposable
{
    TransportState State { get; }

    /// <summary>Wird bei jedem Zustandswechsel ausgelöst (aus einem beliebigen Thread).</summary>
    event EventHandler<TransportState>? StateChanged;

    /// <summary>
    /// Empfangene Rohbytes, so wie sie gelesen wurden (Fragmente und mehrere Frames möglich).
    /// Der Puffer gehört dem Empfänger und wird vom Transport nicht wiederverwendet.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();

    /// <summary>Schreibt Bytes; parallele Aufrufe werden serialisiert.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}
