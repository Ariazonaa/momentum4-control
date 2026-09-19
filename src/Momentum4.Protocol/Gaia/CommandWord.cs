// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Momentum4.Protocol.Gaia;

/// <summary>Pakettyp aus den Bits 8–7 des Command-Words (docs/protocol.md §3.1).</summary>
public enum PacketType : byte
{
    Command = 0,
    Notification = 1,
    Response = 2,
    Error = 3,
}

/// <summary>
/// 16-Bit-Command-Word nach GAIA v3: Bits 15–9 Feature-ID, Bits 8–7 Pakettyp, Bits 6–0 Command-Nummer.
/// </summary>
public readonly record struct CommandWord(ushort Value)
{
    private const ushort TypeMask = 0x0180;

    public byte Feature => (byte)(Value >> 9);

    public PacketType Type => (PacketType)((Value >> 7) & 0x03);

    public byte Number => (byte)(Value & 0x7F);

    /// <summary>Das zugehörige Command-Word vom Typ <see cref="PacketType.Command"/> (Typ-Bits gelöscht).</summary>
    public CommandWord AsCommand() => new((ushort)(Value & ~TypeMask));

    public CommandWord AsNotification() => WithType(PacketType.Notification);

    public CommandWord AsResponse() => WithType(PacketType.Response);

    public CommandWord AsError() => WithType(PacketType.Error);

    public static CommandWord Create(byte feature, PacketType type, byte number)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(feature, (byte)0x7F);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, (byte)0x7F);
        return new CommandWord((ushort)((feature << 9) | ((byte)type << 7) | number));
    }

    public override string ToString() => "0x" + Value.ToString("X4", CultureInfo.InvariantCulture);

    private CommandWord WithType(PacketType type) => new((ushort)((Value & ~TypeMask) | ((byte)type << 7)));
}
