// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Bluetooth.Rfcomm;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Momentum4.Bluetooth.Discovery;

/// <summary>
/// Sucht unter den gekoppelten Bluetooth-Geräten nach MOMENTUM-4-Headsets. Ein gültiger Kandidat braucht den
/// passenden Namen <b>und</b> den GAIA-Dienst; das Headset liefert keine VID/PID (docs/research.md §3).
/// </summary>
public sealed class HeadsetLocator(ILogger<HeadsetLocator> logger) : IHeadsetLocator
{
    public async Task<IReadOnlyList<HeadsetCandidate>> FindAsync(CancellationToken cancellationToken)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);
        logger.LogDebug("{Count} gekoppelte Bluetooth-Geräte gefunden.", infos.Count);

        var candidates = new List<HeadsetCandidate>();
        foreach (var info in infos)
        {
            if (!info.Name.Contains(ProtocolConstants.DeviceNameMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(cancellationToken);
            if (device is null)
            {
                logger.LogWarning("Gerät '{Name}' ist gekoppelt, lässt sich aber nicht öffnen.", info.Name);
                continue;
            }

            var connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;

            // Eine SDP-Abfrage über Funk nur, wenn das Headset gerade verbunden ist.
            var resolved = await GaiaServiceResolver.ResolveAsync(device, allowUncached: connected, logger, cancellationToken);
            resolved?.Service.Dispose();

            var candidate = new HeadsetCandidate(new BluetoothAddress(device.BluetoothAddress), device.Name, connected, resolved is not null);
            logger.LogInformation(
                "Kandidat '{Name}' {Address}: Windows-Verbindung {Connected}, GAIA-Dienst {HasGaia}.",
                candidate.Name, candidate.Address, connected ? "ja" : "nein", candidate.HasGaiaService ? "ja" : "nein");
            candidates.Add(candidate);
        }

        return candidates;
    }
}
