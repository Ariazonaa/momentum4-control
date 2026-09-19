// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Transport;

/// <summary>Änderung, die ein sofortiger Verbindungsversuch rechtfertigen kann.</summary>
/// <param name="HeadsetConnected">Windows meldet die Bluetooth-Verbindung des Headsets (null = keine Aussage).</param>
/// <param name="RadioOn">Bluetooth-Radio des PCs an/aus (null = keine Aussage).</param>
public sealed record PresenceChange(bool? HeadsetConnected, bool? RadioOn);

/// <summary>Beobachtet, ob das Headset erreichbar ist (docs/architecture.md §5): Verbindungsstatus und Bluetooth-Radio.</summary>
public interface IHeadsetPresence : IDisposable
{
    event EventHandler<PresenceChange>? Changed;

    Task StartAsync(BluetoothAddress address, CancellationToken cancellationToken);
}
