// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Bluetooth.Sdp;

/// <summary>Auswertung der rohen SDP-Attribute eines Dienstes (RFCOMM-Kanal, Dienstname).</summary>
public sealed class SdpServiceRecord
{
    public const uint ServiceClassIdListAttribute = 0x0001;
    public const uint ProtocolDescriptorListAttribute = 0x0004;
    public const uint ServiceNameAttribute = 0x0100; // Language-Base 0x0100 + Offset 0x0000

    private static readonly Guid RfcommProtocolUuid = SdpElement.FromShortUuid(0x0003);

    public SdpServiceRecord(IReadOnlyDictionary<uint, byte[]> rawAttributes)
    {
        RawAttributes = rawAttributes;
    }

    public IReadOnlyDictionary<uint, byte[]> RawAttributes { get; }

    /// <summary>RFCOMM-Kanal aus der ProtocolDescriptorList, sonst <c>null</c>.</summary>
    public byte? RfcommChannel
    {
        get
        {
            var list = TryParse(ProtocolDescriptorListAttribute);
            if (list?.Type != SdpElementType.Sequence)
            {
                return null;
            }

            foreach (var protocol in list.Children)
            {
                if (protocol.Type == SdpElementType.Sequence
                    && protocol.Children.Count >= 2
                    && protocol.Children[0].Type == SdpElementType.Uuid
                    && protocol.Children[0].AsUuid() == RfcommProtocolUuid
                    && protocol.Children[1].Type == SdpElementType.UnsignedInteger)
                {
                    return (byte)protocol.Children[1].AsUnsigned();
                }
            }

            return null;
        }
    }

    /// <summary>Dienstname (Attribut 0x0100), beim MOMENTUM 4 erwartet: „GAIA“.</summary>
    public string? ServiceName
    {
        get
        {
            var element = TryParse(ServiceNameAttribute);
            return element?.Type == SdpElementType.Text ? element.AsText() : null;
        }
    }

    /// <summary>Lesbare Darstellung eines Attributs oder <c>null</c>, wenn es nicht dekodierbar ist.</summary>
    public string? Describe(uint attributeId) => TryParse(attributeId)?.ToString();

    private SdpElement? TryParse(uint attributeId)
    {
        if (!RawAttributes.TryGetValue(attributeId, out var raw))
        {
            return null;
        }

        try
        {
            return SdpParser.Parse(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
