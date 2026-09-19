// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Momentum4.Bluetooth.Sdp;

/// <summary>
/// Dekodiert SDP-Datenelemente, wie Windows sie über <c>RfcommDeviceService.GetSdpRawAttributesAsync</c> liefert
/// (ein Attributwert = genau ein Datenelement inklusive Header).
/// </summary>
public static class SdpParser
{
    private const int MaxDepth = 16;

    public static SdpElement Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var element = ParseElement(data, ref offset, depth: 0);
        if (offset != data.Length)
        {
            throw new FormatException($"{data.Length - offset} überzählige Byte nach dem SDP-Element.");
        }

        return element;
    }

    private static SdpElement ParseElement(ReadOnlySpan<byte> data, ref int offset, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("SDP-Sequenzen zu tief verschachtelt.");
        }

        var header = Take(data, ref offset, 1)[0];
        var typeValue = header >> 3;
        if (typeValue > (int)SdpElementType.Url)
        {
            throw new FormatException($"Unbekannter SDP-Typ {typeValue}.");
        }

        var type = (SdpElementType)typeValue;
        var sizeIndex = header & 0x07;
        var length = sizeIndex switch
        {
            0 => type == SdpElementType.Nil ? 0 : 1,
            1 => 2,
            2 => 4,
            3 => 8,
            4 => 16,
            5 => Take(data, ref offset, 1)[0],
            6 => BinaryPrimitives.ReadUInt16BigEndian(Take(data, ref offset, 2)),
            _ => checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(data, ref offset, 4))),
        };

        var body = Take(data, ref offset, length);
        if (type is SdpElementType.Sequence or SdpElementType.Alternative)
        {
            var children = new List<SdpElement>();
            var inner = 0;
            while (inner < body.Length)
            {
                children.Add(ParseElement(body, ref inner, depth + 1));
            }

            return new SdpElement(type, [], children);
        }

        return new SdpElement(type, body.ToArray(), []);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset + count > data.Length)
        {
            throw new FormatException($"SDP-Daten zu kurz: {count} Byte ab Offset {offset} erwartet, {data.Length - offset} vorhanden.");
        }

        var slice = data.Slice(offset, count);
        offset += count;
        return slice;
    }
}
