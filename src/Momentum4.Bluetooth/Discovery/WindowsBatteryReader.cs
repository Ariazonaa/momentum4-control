// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Momentum4.Core.Transport;
using Windows.Devices.Bluetooth;

namespace Momentum4.Bluetooth.Discovery;

/// <summary>
/// Akku über Windows statt GAIA (docs/architecture.md §5, <c>WindowsBatteryFallback</c>): die Geräte-Eigenschaft
/// <c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c> (Byte) am Knoten „… Hands-Free AG“, der dieselbe Container-ID hat wie
/// der Knoten <c>BTHENUM\DEV_&lt;Adresse&gt;</c>. Das Headset meldet den Wert über HFP; am eigenen Gerät stimmte er mit
/// GAIA überein (Stufe 3 und 2026-09-19: 80 %). Liefert nur etwas, solange Windows das Headset als verbunden meldet –
/// sonst stünde dort der letzte, alte Wert.
/// Gelesen wird über cfgmgr32: <c>DeviceInformation</c> mit zusätzlichen Eigenschaften scheitert unter Native AOT
/// (CsWinRT kann das <c>string[]</c> nicht als <c>IIterable&lt;string&gt;</c> übergeben, 2026-09-19).
/// </summary>
public sealed partial class WindowsBatteryReader(BluetoothAddress address, ILogger<WindowsBatteryReader> logger)
{
    private static readonly DevPropKey BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    private static readonly DevPropKey ContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    private const uint CrSuccess = 0;
    private const uint FilterEnumerator = 0x1, FilterPresent = 0x100;
    private const uint TypeByte = 0x03, TypeGuid = 0x0D;

    private string? _nodeId;
    private bool _failureLogged;

    public async Task<int?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var device = await BluetoothDevice.FromBluetoothAddressAsync(address.Value).AsTask(cancellationToken);
            if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                return null;
            }

            if (_nodeId is { } cached && ReadBattery(cached) is { } value)
            {
                return value;
            }

            _nodeId = null;
            var nodes = PresentBluetoothNodes();
            var deviceNode = nodes.FirstOrDefault(id => id.StartsWith($@"BTHENUM\DEV_{address.Value:X12}", StringComparison.OrdinalIgnoreCase));
            if (deviceNode is null || ReadContainerId(deviceNode) is not { } container)
            {
                logger.LogDebug("Akku über Windows: kein Geräteknoten für {Address}.", address);
                return null;
            }

            foreach (var node in nodes)
            {
                if (ReadContainerId(node) == container && ReadBattery(node) is { } percent)
                {
                    _nodeId = node;
                    logger.LogDebug("Akku über Windows: Knoten {Node}.", node);
                    return percent;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Beim ersten Mal mit Grund ins Log, danach nur noch Debug (die App fragt alle 30 s).
            logger.Log(_failureLogged ? LogLevel.Debug : LogLevel.Information, "Akku über Windows nicht lesbar: {Reason}", BluetoothErrors.Describe(ex));
            _failureLogged = true;
            return null;
        }
    }

    private static List<string> PresentBluetoothNodes()
    {
        const uint flags = FilterEnumerator | FilterPresent;
        if (CM_Get_Device_ID_List_SizeW(out var length, "BTHENUM", flags) != CrSuccess || length == 0)
        {
            return [];
        }

        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW("BTHENUM", buffer, length, flags) != CrSuccess)
        {
            return [];
        }

        return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static int? ReadBattery(string instanceId) =>
        ReadProperty(instanceId, BatteryKey, TypeByte, 1) is [var percent] && percent <= 100 ? percent : null;

    private static Guid? ReadContainerId(string instanceId) =>
        ReadProperty(instanceId, ContainerIdKey, TypeGuid, 16) is { Length: 16 } bytes ? new Guid(bytes) : null;

    private static byte[]? ReadProperty(string instanceId, DevPropKey key, uint expectedType, int size)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != CrSuccess)
        {
            return null;
        }

        var buffer = new byte[size];
        var length = (uint)size;
        return CM_Get_DevNode_PropertyW(devInst, key, out var type, buffer, ref length, 0) == CrSuccess && type == expectedType && length == size
            ? buffer
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devInst, in DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
}
