// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Momentum4.Protocol.Features;

public readonly record struct GaiaApiVersion(int Major, int Minor)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}

/// <summary>Decoder für den Qualcomm-Core (Vendor <c>0x001D</c>, Feature 0), docs/protocol.md §5.1.</summary>
public static class CoreCodec
{
    /// <summary><c>0x0000</c> → <c>0x0100 [major u8, minor u8]</c>.</summary>
    public static GaiaApiVersion DecodeApiVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ProtocolFormatException($"API-Version: 2 Byte erwartet, {payload.Length} erhalten", payload);
        }

        return new GaiaApiVersion(payload[0], payload[1]);
    }

    /// <summary><c>0x0003</c> → <c>0x0103 [STRING]</c>.</summary>
    public static string DecodeSerialNumber(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ProtocolFormatException("Seriennummer: leere Antwort", payload);
        }

        return VersionsCodec.DecodeString(payload);
    }
}
