// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Momentum4.Protocol.Gaia;

/// <summary>Ein vollständiger GAIA-Frame, wie er auf dem RFCOMM-Kanal läuft.</summary>
public sealed class GaiaFrame
{
    public GaiaFrame(byte version, byte flags, ushort vendor, CommandWord command, byte[] payload, byte[] raw)
    {
        Version = version;
        Flags = flags;
        Vendor = vendor;
        Command = command;
        Payload = payload;
        Raw = raw;
    }

    public byte Version { get; }

    /// <summary>Flags-Byte (Bit 0: Checksumme, Bit 1: 2-Byte-Länge). Beim M4 bisher immer 0 erwartet.</summary>
    public byte Flags { get; }

    public ushort Vendor { get; }

    public CommandWord Command { get; }

    public PacketType Type => Command.Type;

    public byte[] Payload { get; }

    /// <summary>Alle Bytes des Frames inklusive Header (für Logs und Mitschnitte).</summary>
    public byte[] Raw { get; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Vendor:X4}:{Command.Value:X4} {Type} [{Hex.Format(Payload)}]");
}
