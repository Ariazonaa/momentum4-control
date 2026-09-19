// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Features;

/// <summary>Decoder für Feature 3 (battery), docs/protocol.md §6.2.</summary>
public static class BatteryCodec
{
    /// <summary>
    /// <c>0x0603</c> → <c>0x0703 [percent u8]</c> (auch Notification <c>0x0683</c>). Laut <c>m4.json</c> können weitere
    /// Bytes für weitere Geräte folgen (Earbuds); der erste Wert ist das Headset selbst.
    /// </summary>
    public static int DecodeLevel(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ProtocolFormatException("Akku: leere Antwort", payload);
        }

        var percent = payload[0];
        if (percent > 100)
        {
            throw new ProtocolFormatException($"Akku: {percent} % ist kein gültiger Wert", payload);
        }

        return percent;
    }

    /// <summary>Timer 0 = Auto-Power-Off (docs/protocol.md §6.2).</summary>
    public const byte TimerAutoPowerOff = 0;

    /// <summary><c>0x0600 [timer, seconds u16]</c> (Big Endian wie die Antwort von <c>0x0601</c>).</summary>
    public static byte[] EncodeTimer(byte timer, int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, ushort.MaxValue);
        return [timer, (byte)(seconds >> 8), (byte)seconds];
    }

    /// <summary><c>0x0601 [timer]</c> → <c>[timer, seconds u16]</c>; das Timer-Echo muss passen. 0 s = nie.</summary>
    public static int DecodeTimerSeconds(ReadOnlySpan<byte> payload, byte timer)
    {
        if (payload.Length != 3 || payload[0] != timer)
        {
            throw new ProtocolFormatException($"Timer {timer}: genau [{timer}, u16] erwartet", payload);
        }

        return (payload[1] << 8) | payload[2];
    }
}
