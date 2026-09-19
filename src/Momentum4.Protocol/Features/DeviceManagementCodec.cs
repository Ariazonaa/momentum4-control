// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Momentum4.Protocol.Features;

/// <summary>Eintrag der Liste gekoppelter Geräte (<c>0x1401</c>). Adressiert nur über den Index, ohne MAC.</summary>
public sealed record PairedDeviceEntry(int Index, byte Priority, byte ConnectionStatus, string Name)
{
    public bool IsConnected => ConnectionStatus != 0;
}

/// <summary>Decoder für Feature 10 (deviceManagement / Multipoint), docs/protocol.md §6.6.</summary>
public static class DeviceManagementCodec
{
    /// <summary>Plausibilitätsgrenze wie in [MC] (<c>maximumSaneCount</c>).</summary>
    public const int MaxSaneCount = 64;

    /// <summary><c>0x1400</c> → <c>[count u16]</c>.</summary>
    public static int DecodeCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 2)
        {
            throw new ProtocolFormatException("Anzahl gekoppelter Geräte: 2 Byte erwartet", payload);
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (count > MaxSaneCount)
        {
            throw new ProtocolFormatException($"Anzahl gekoppelter Geräte {count} unplausibel", payload);
        }

        return count;
    }

    /// <summary><c>0x1401 [index]</c> → <c>[index, priority, status, name…\0]</c>; das Index-Echo muss passen.</summary>
    public static PairedDeviceEntry DecodeEntry(ReadOnlySpan<byte> payload, int expectedIndex)
    {
        if (payload.Length < 3)
        {
            throw new ProtocolFormatException("Geräteeintrag: mindestens 3 Byte erwartet", payload);
        }

        if (payload[0] != expectedIndex)
        {
            throw new ProtocolFormatException($"Geräteeintrag: Index-Echo {payload[0]} statt {expectedIndex}", payload);
        }

        var name = payload.Length > 3 ? VersionsCodec.DecodeString(payload[3..]) : string.Empty;
        return new PairedDeviceEntry(payload[0], payload[1], payload[2], name);
    }

    /// <summary><c>0x1402</c> (Verbinden) und <c>0x1404</c> (Status) ← <c>[index]</c>.</summary>
    public static byte[] EncodeIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxSaneCount);
        return [(byte)index];
    }

    /// <summary>
    /// <c>0x1404 [index]</c> → <c>[index, status]</c>; status ≠ 0 = verbunden, das Index-Echo muss passen ([MC]).
    /// </summary>
    public static bool DecodeConnectionStatus(ReadOnlySpan<byte> payload, int expectedIndex)
    {
        if (payload.Length != 2)
        {
            throw new ProtocolFormatException("Verbindungsstatus: genau [index, status] erwartet", payload);
        }

        if (payload[0] != expectedIndex)
        {
            throw new ProtocolFormatException($"Verbindungsstatus: Index-Echo {payload[0]} statt {expectedIndex}", payload);
        }

        return payload[1] != 0;
    }

    /// <summary><c>0x1407</c> → <c>[index]</c>.</summary>
    public static int DecodeOwnIndex(ReadOnlySpan<byte> payload) => DecodeSingleByte(payload, "Eigener Index");

    /// <summary><c>0x1409</c> → <c>[n]</c>.</summary>
    public static int DecodeMaxConnections(ReadOnlySpan<byte> payload) => DecodeSingleByte(payload, "Max. Verbindungen");

    private static int DecodeSingleByte(ReadOnlySpan<byte> payload, string what)
    {
        if (payload.Length != 1)
        {
            throw new ProtocolFormatException($"{what}: genau 1 Byte erwartet", payload);
        }

        return payload[0];
    }
}
