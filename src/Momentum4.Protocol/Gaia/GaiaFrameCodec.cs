// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Gaia;

/// <summary>
/// Baut Frames zum Senden. Immer <c>FF 03 00 LL</c> + Vendor + Command + Payload, ohne Checksumme – so senden es
/// alle bekannten MOMENTUM-4-Implementierungen (docs/protocol.md §2.2).
/// </summary>
public static class GaiaFrameCodec
{
    public const byte StartOfFrame = 0xFF;
    public const byte TxVersion = 0x03;
    public const int MaxTxPayload = 255;

    public static byte[] Encode(ushort vendor, CommandWord command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxTxPayload)
        {
            throw new ArgumentException($"Payload mit {payload.Length} Byte ist zu lang (max. {MaxTxPayload}).", nameof(payload));
        }

        var frame = new byte[8 + payload.Length];
        frame[0] = StartOfFrame;
        frame[1] = TxVersion;
        frame[2] = 0x00; // Flags: keine Checksumme, 1-Byte-Länge
        frame[3] = (byte)payload.Length;
        frame[4] = (byte)(vendor >> 8);
        frame[5] = (byte)vendor;
        frame[6] = (byte)(command.Value >> 8);
        frame[7] = (byte)command.Value;
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }
}
