// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>Einfache Ein/Aus-Werte (1 Byte, 0 oder 1), wie sie viele Getter liefern.</summary>
public static class SwitchCodec
{
    public static bool Decode(ReadOnlySpan<byte> payload, string what)
    {
        if (payload.Length != 1 || payload[0] > 1)
        {
            throw new ProtocolFormatException($"{what}: genau 1 Byte mit 0 oder 1 erwartet", payload);
        }

        return payload[0] == 1;
    }

    public static byte[] Encode(bool value) => [value ? (byte)1 : (byte)0];
}
