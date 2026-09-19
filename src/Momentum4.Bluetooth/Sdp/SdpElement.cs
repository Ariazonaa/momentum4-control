// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;

namespace Momentum4.Bluetooth.Sdp;

/// <summary>Datentypen eines SDP-Datenelements (Bluetooth Core Spec, Vol 3, Part B, 3.2).</summary>
public enum SdpElementType : byte
{
    Nil = 0,
    UnsignedInteger = 1,
    SignedInteger = 2,
    Uuid = 3,
    Text = 4,
    Boolean = 5,
    Sequence = 6,
    Alternative = 7,
    Url = 8,
}

/// <summary>Ein dekodiertes SDP-Datenelement. Sequenzen enthalten ihre Kinder, alle anderen Typen ihre Rohdaten.</summary>
public sealed class SdpElement
{
    private static readonly Guid BluetoothBaseUuid = new("00000000-0000-1000-8000-00805F9B34FB");

    internal SdpElement(SdpElementType type, byte[] data, IReadOnlyList<SdpElement> children)
    {
        Type = type;
        Data = data;
        Children = children;
    }

    public SdpElementType Type { get; }

    /// <summary>Rohdaten des Werts (bei Sequenzen leer).</summary>
    public IReadOnlyList<byte> Data { get; }

    public IReadOnlyList<SdpElement> Children { get; }

    public ulong AsUnsigned()
    {
        Expect(SdpElementType.UnsignedInteger);
        var d = (byte[])Data;
        return d.Length switch
        {
            1 => d[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(d),
            4 => BinaryPrimitives.ReadUInt32BigEndian(d),
            8 => BinaryPrimitives.ReadUInt64BigEndian(d),
            _ => throw new FormatException($"Unsigned Integer mit {d.Length} Byte wird nicht unterstützt."),
        };
    }

    /// <summary>UUID; 16- und 32-Bit-Kurzformen werden auf die Bluetooth-Basis-UUID erweitert.</summary>
    public Guid AsUuid()
    {
        Expect(SdpElementType.Uuid);
        var d = (byte[])Data;
        return d.Length switch
        {
            2 => FromShortUuid(BinaryPrimitives.ReadUInt16BigEndian(d)),
            4 => FromShortUuid(BinaryPrimitives.ReadUInt32BigEndian(d)),
            16 => new Guid(d, bigEndian: true),
            _ => throw new FormatException($"UUID mit {d.Length} Byte ist ungültig."),
        };
    }

    public string AsText()
    {
        if (Type is not (SdpElementType.Text or SdpElementType.Url))
        {
            throw new InvalidOperationException($"Element ist {Type}, nicht Text.");
        }

        return Encoding.UTF8.GetString((byte[])Data).TrimEnd('\0');
    }

    public static Guid FromShortUuid(uint value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BluetoothBaseUuid.TryWriteBytes(bytes, bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new Guid(bytes, bigEndian: true);
    }

    public override string ToString() => Type switch
    {
        SdpElementType.Sequence or SdpElementType.Alternative => $"{Type}[{string.Join(", ", Children)}]",
        SdpElementType.UnsignedInteger => $"uint:{AsUnsigned()}",
        SdpElementType.Uuid => $"uuid:{AsUuid()}",
        SdpElementType.Text or SdpElementType.Url => $"\"{AsText()}\"",
        SdpElementType.Nil => "nil",
        _ => $"{Type}:{Convert.ToHexString((byte[])Data)}",
    };

    private void Expect(SdpElementType expected)
    {
        if (Type != expected)
        {
            throw new InvalidOperationException($"Element ist {Type}, erwartet {expected}.");
        }
    }
}
