// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Catalog;

/// <summary>
/// Kurze, lesbare Deutung eines Frames für den Protokoll-Inspektor (Spezifikation §22). Nur für verifizierte Formate aus
/// docs/protocol.md; alles andere bleibt <c>null</c> – geraten wird nicht.
/// </summary>
public static class FrameDescriber
{
    public static string? Describe(GaiaFrame frame) => Describe(frame.Vendor, frame.Command, frame.Payload);

    public static string? Describe(ushort vendor, CommandWord command, ReadOnlySpan<byte> payload)
    {
        switch (command.Type)
        {
            case PacketType.Error:
                return payload.Length > 0 ? $"abgelehnt, Reason 0x{payload[0]:X2}" : "abgelehnt";
            case PacketType.Command:
                return null;
        }

        try
        {
            return vendor switch
            {
                ProtocolConstants.VendorQualcomm when command.AsCommand().Value == 0x0000 => $"GAIA-API {CoreCodec.DecodeApiVersion(payload)}",
                ProtocolConstants.VendorSennheiser => DescribeSennheiser(command, payload),
                _ => null,
            };
        }
        catch (ProtocolFormatException ex)
        {
            return $"nicht dekodierbar: {ex.Message}";
        }
    }

    private static string? DescribeSennheiser(CommandWord command, ReadOnlySpan<byte> payload)
    {
        // Notification 0x1082 trägt alle Gains (wie die Antwort auf 0x1003), nicht ein Band wie 0x1002.
        if (command.Type == PacketType.Notification && command.Value == 0x1082)
        {
            return Gains(payload);
        }

        return command.AsCommand().Value switch
        {
            0x0603 => $"Akku {BatteryCodec.DecodeLevel(payload)} %",
            0x0602 => SwitchCodec.Decode(payload, "Ladekabel") ? "Ladekabel angesteckt" : "Ladekabel ab",
            0x0804 => $"Sound-Mode {GenericAudioCodec.DecodeSoundMode(payload)}",
            0x1000 => Describe(UserEqCodec.DecodeConfig(payload)),
            0x1003 => Gains(payload),
            0x1009 => $"Bass Boost {OnOff(UserEqCodec.DecodeBassBoost(payload))}",
            0x1201 => $"Firmware {VersionsCodec.DecodeFirmwareVersion(payload)}",
            0x1206 => $"Modell „{VersionsCodec.DecodeModelId(payload)}“",
            0x1400 => $"{DeviceManagementCodec.DecodeCount(payload)} gekoppelte Geräte",
            0x1401 when payload.Length > 0 => Describe(DeviceManagementCodec.DecodeEntry(payload, payload[0])),
            0x1404 when payload.Length > 0 => $"Gerät #{payload[0]} {(DeviceManagementCodec.DecodeConnectionStatus(payload, payload[0]) ? "verbunden" : "nicht verbunden")}",
            0x1407 => $"eigener Platz #{DeviceManagementCodec.DecodeOwnIndex(payload)}",
            0x1409 => $"max. {DeviceManagementCodec.DecodeMaxConnections(payload)} Verbindungen",
            0x1805 => $"Transparent Hearing {OnOff(SwitchCodec.Decode(payload, "Transparent Hearing"))}",
            0x1A01 => Describe(AncCodec.DecodeModes(payload)),
            0x1A03 => $"Pegel {AncCodec.DecodeLevel(payload)} (0 = stärkstes ANC)",
            0x1A05 => $"ANC {OnOff(AncCodec.DecodeEnabled(payload))}",
            _ when payload.Length == 0 && command.Type == PacketType.Response => "bestätigt",
            _ => null,
        };
    }

    private static string Gains(ReadOnlySpan<byte> payload) =>
        "EQ " + string.Join(" / ", UserEqCodec.DecodeAllGains(payload, payload.Length).Select(g => g.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture))) + " dB";

    private static string OnOff(bool on) => on ? "an" : "aus";

    private static string Describe(AncModeTable t) => $"Anti-Wind {t.AntiWind?.ToString() ?? "?"}, Adaptive {(t.Adaptive is { } a ? OnOff(a) : "?")}";

    private static string Describe(EqConfig c) =>
        $"{c.BandCount} Bänder, {c.MinGainDb.ToString("0.0", CultureInfo.InvariantCulture)} … {c.MaxGainDb.ToString("+0.0", CultureInfo.InvariantCulture)} dB";

    private static string Describe(PairedDeviceEntry e) => $"Gerät #{e.Index} „{e.Name}“{(e.IsConnected ? ", verbunden" : string.Empty)}";
}
