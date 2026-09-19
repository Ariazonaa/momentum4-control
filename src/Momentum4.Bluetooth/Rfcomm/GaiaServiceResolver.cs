// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Momentum4.Bluetooth.Sdp;
using Momentum4.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;

namespace Momentum4.Bluetooth.Rfcomm;

/// <summary>Was Windows über den GAIA-Dienst eines Geräts weiß (ohne dass ein Kanal geöffnet wird).</summary>
/// <param name="HostName">Ziel für <c>StreamSocket.ConnectAsync</c> (Raw-Name des Hosts).</param>
/// <param name="ServiceName">Dienstbezeichner für <c>StreamSocket.ConnectAsync</c>.</param>
/// <param name="CacheMode">Aus welchem Modus der Dienst stammt (Cached oder Uncached).</param>
/// <param name="Sdp">Rohe SDP-Attribute mit Auswertung (Kanal, Name), <c>null</c> wenn nicht lesbar.</param>
public sealed record GaiaServiceInfo(string HostName, string ServiceName, BluetoothCacheMode CacheMode, SdpServiceRecord? Sdp);

/// <summary>
/// Löst den RFCOMM-Dienst „GAIA“ eines Geräts über seine UUID auf – zuerst aus dem Windows-Cache, dann per
/// SDP-Abfrage. Es wird nie ein Kanal geraten oder durchprobiert (docs/protocol.md §1).
/// </summary>
public static class GaiaServiceResolver
{
    private static readonly RfcommServiceId GaiaServiceId = RfcommServiceId.FromUuid(ProtocolConstants.GaiaServiceUuid);

    /// <summary>
    /// Liefert den Dienst oder <c>null</c>. Der Aufrufer besitzt das Ergebnis und muss es freigeben.
    /// <paramref name="allowUncached"/> = false vermeidet eine SDP-Abfrage über Funk (z. B. bei ausgeschaltetem Headset).
    /// Der MOMENTUM 4 meldet zwei GAIA-Einträge (RFCOMM-Kanal 1 und 2, docs/protocol.md §1);
    /// <paramref name="serviceIndex"/> wählt den Eintrag in der Reihenfolge, die Windows liefert.
    /// </summary>
    public static async Task<(RfcommDeviceService Service, BluetoothCacheMode Mode)?> ResolveAsync(
        BluetoothDevice device, bool allowUncached, ILogger logger, CancellationToken cancellationToken, int serviceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(serviceIndex);

        BluetoothCacheMode[] modes = allowUncached
            ? [BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached]
            : [BluetoothCacheMode.Cached];

        foreach (var mode in modes)
        {
            var result = await device.GetRfcommServicesForIdAsync(GaiaServiceId, mode).AsTask(cancellationToken);
            if (result.Error == BluetoothError.Success && result.Services.Count > serviceIndex)
            {
                for (var i = 0; i < result.Services.Count; i++)
                {
                    if (i != serviceIndex)
                    {
                        result.Services[i].Dispose();
                    }
                }

                logger.LogDebug("GAIA-Dienst gefunden ({Mode}, {Count} Eintrag/Einträge, verwende Index {Index}).", mode, result.Services.Count, serviceIndex);
                return (result.Services[serviceIndex], mode);
            }

            foreach (var service in result.Services)
            {
                service.Dispose();
            }

            logger.LogDebug("GAIA-Dienst nicht gefunden ({Mode}): Fehler {Error}, {Count} Einträge.", mode, result.Error, result.Services.Count);
        }

        return null;
    }

    /// <summary>Liest die SDP-Attribute des Dienstes. Fehler werden protokolliert und ergeben <c>null</c>.</summary>
    public static async Task<SdpServiceRecord?> ReadSdpAsync(RfcommDeviceService service, ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var mode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
        {
            try
            {
                var raw = await service.GetSdpRawAttributesAsync(mode).AsTask(cancellationToken);
                if (raw.Count == 0)
                {
                    logger.LogDebug("SDP-Attribute ({Mode}) leer.", mode);
                    continue;
                }

                var attributes = raw.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
                return new SdpServiceRecord(attributes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "SDP-Attribute ({Mode}) konnten nicht gelesen werden.", mode);
            }
        }

        return null;
    }

    /// <summary>
    /// Liest alle Einträge, die Windows für die GAIA-UUID meldet, samt SDP-Attributen (nur Diagnose, öffnet keinen Kanal).
    /// </summary>
    public static async Task<IReadOnlyList<GaiaServiceInfo>> InspectAllAsync(BluetoothDevice device, BluetoothCacheMode mode, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await device.GetRfcommServicesForIdAsync(GaiaServiceId, mode).AsTask(cancellationToken);
        logger.LogDebug("GetRfcommServicesForIdAsync({Mode}): Fehler {Error}, {Count} Einträge.", mode, result.Error, result.Services.Count);

        var infos = new List<GaiaServiceInfo>();
        foreach (var service in result.Services)
        {
            using (service)
            {
                var sdp = await ReadSdpAsync(service, logger, cancellationToken);
                infos.Add(new GaiaServiceInfo(service.ConnectionHostName.RawName, service.ConnectionServiceName, mode, sdp));
            }
        }

        return infos;
    }
}
