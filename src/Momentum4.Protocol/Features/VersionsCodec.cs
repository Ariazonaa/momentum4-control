// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Momentum4.Protocol.Features;

/// <summary>Firmware-Version des Headsets, z. B. 3.38.3.</summary>
public readonly record struct FirmwareVersion(int Major, int Minor, int Patch) : IComparable<FirmwareVersion>
{
    public int CompareTo(FirmwareVersion other) =>
        (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(FirmwareVersion left, FirmwareVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

/// <summary>Decoder für Feature 9 (versions), docs/protocol.md §6.5.</summary>
public static class VersionsCodec
{
    /// <summary><c>0x1201</c> → <c>0x1301 [major u16, minor u16, patch u16]</c>.</summary>
    public static FirmwareVersion DecodeFirmwareVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 6)
        {
            throw new ProtocolFormatException($"Firmware-Version: 6 Byte erwartet, {payload.Length} erhalten", payload);
        }

        return new FirmwareVersion(
            BinaryPrimitives.ReadUInt16BigEndian(payload),
            BinaryPrimitives.ReadUInt16BigEndian(payload[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[4..]));
    }

    /// <summary><c>0x1206</c> → <c>0x1306 [STRING]</c>; UTF-8, abschließende Nullbytes werden entfernt.</summary>
    public static string DecodeModelId(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ProtocolFormatException("Modell-ID: leere Antwort", payload);
        }

        return DecodeString(payload);
    }

    internal static string DecodeString(ReadOnlySpan<byte> payload)
    {
        var end = payload.IndexOf((byte)0);
        var text = end >= 0 ? payload[..end] : payload;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(text);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProtocolFormatException($"Kein gültiges UTF-8 ({ex.Message})", payload);
        }
    }
}
