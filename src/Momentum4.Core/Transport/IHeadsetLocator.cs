// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Transport;

/// <summary>Ein gekoppeltes Gerät, das nach Namen ein MOMENTUM 4 sein könnte.</summary>
/// <param name="Address">Bluetooth-Adresse.</param>
/// <param name="Name">Name laut Windows.</param>
/// <param name="IsConnected">Windows meldet eine aktive Bluetooth-Verbindung (unabhängig von GAIA).</param>
/// <param name="HasGaiaService">Das Gerät bietet den RFCOMM-Dienst „GAIA“ an. Nur dann ist es ein gültiger Kandidat.</param>
public sealed record HeadsetCandidate(BluetoothAddress Address, string Name, bool IsConnected, bool HasGaiaService);

/// <summary>Findet gekoppelte MOMENTUM-4-Headsets (Name und GAIA-Dienst, nicht der Name allein).</summary>
public interface IHeadsetLocator
{
    Task<IReadOnlyList<HeadsetCandidate>> FindAsync(CancellationToken cancellationToken);
}
