// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Bluetooth.Sdp;
using Momentum4.Protocol;

namespace Momentum4.Bluetooth.Tests;

// Die Byte-Folgen sind nach der Bluetooth-Core-Spezifikation (Vol 3, Part B, 3.2) von Hand gebaut,
// keine Mitschnitte vom Headset.
public sealed class SdpParserTests
{
    // Sequenz { Sequenz { UUID16 0x0100 (L2CAP) }, Sequenz { UUID16 0x0003 (RFCOMM), uint8 15 } }
    private const string ProtocolDescriptorListChannel15 = "35 0C 35 03 19 01 00 35 05 19 00 03 08 0F";

    [Fact]
    public void Parses_protocol_descriptor_list()
    {
        var element = SdpParser.Parse(Hex.Parse(ProtocolDescriptorListChannel15));

        Assert.Equal(SdpElementType.Sequence, element.Type);
        Assert.Equal(2, element.Children.Count);
        Assert.Equal(SdpElement.FromShortUuid(0x0100), element.Children[0].Children[0].AsUuid());
        Assert.Equal(15UL, element.Children[1].Children[1].AsUnsigned());
    }

    [Fact]
    public void Service_record_reports_rfcomm_channel_and_name()
    {
        var record = new SdpServiceRecord(new Dictionary<uint, byte[]>
        {
            [SdpServiceRecord.ProtocolDescriptorListAttribute] = Hex.Parse(ProtocolDescriptorListChannel15),
            [SdpServiceRecord.ServiceNameAttribute] = Hex.Parse("25 04 47 41 49 41"), // "GAIA"
        });

        Assert.Equal((byte)15, record.RfcommChannel);
        Assert.Equal("GAIA", record.ServiceName);
    }

    [Fact]
    public void Service_record_without_rfcomm_returns_null_channel()
    {
        var record = new SdpServiceRecord(new Dictionary<uint, byte[]>
        {
            // nur L2CAP
            [SdpServiceRecord.ProtocolDescriptorListAttribute] = Hex.Parse("35 05 35 03 19 01 00"),
        });

        Assert.Null(record.RfcommChannel);
        Assert.Null(record.ServiceName);
    }

    [Fact]
    public void Service_record_ignores_undecodable_attributes()
    {
        var record = new SdpServiceRecord(new Dictionary<uint, byte[]>
        {
            [SdpServiceRecord.ProtocolDescriptorListAttribute] = Hex.Parse("35 0C 35 03"),
        });

        Assert.Null(record.RfcommChannel);
        Assert.Null(record.Describe(SdpServiceRecord.ProtocolDescriptorListAttribute));
    }

    [Fact]
    public void Parses_128_bit_uuid_big_endian()
    {
        var element = SdpParser.Parse(Hex.Parse("35 11 1C A2 12 9F F3 08 1B 4C 45 8A FE 46 9D 9C 48 42 EC"));

        Assert.Equal(ProtocolConstants.GaiaServiceUuid, element.Children[0].AsUuid());
    }

    [Fact]
    public void Expands_short_uuid_with_bluetooth_base()
    {
        Assert.Equal(new Guid("00000003-0000-1000-8000-00805F9B34FB"), SdpElement.FromShortUuid(0x0003));
    }

    [Fact]
    public void Parses_two_byte_length_prefix()
    {
        // Text mit 16-Bit-Längenfeld (Size-Index 6)
        var element = SdpParser.Parse(Hex.Parse("26 00 04 47 41 49 41"));

        Assert.Equal("GAIA", element.AsText());
    }

    // Echte SDP-Attribute des MOMENTUM 4, gelesen mit `m4poc sdp` am 2026-09-18 (Windows-Cache und Uncached
    // identisch; Firmware noch nicht ausgelesen – kommt mit Phase C). Das Headset meldet zwei solche Einträge,
    // hier Eintrag 0 (Kanal 1); Eintrag 1 unterscheidet sich nur in Handle 0x00010006 und Kanal 2.
    [Theory]
    [InlineData("0A 00 01 00 04", "35 0C 35 03 19 01 00 35 05 19 00 03 08 01", 1)]
    [InlineData("0A 00 01 00 06", "35 0C 35 03 19 01 00 35 05 19 00 03 08 02", 2)]
    public void Parses_real_momentum4_gaia_records(string handle, string protocolDescriptorList, byte expectedChannel)
    {
        var record = new SdpServiceRecord(new Dictionary<uint, byte[]>
        {
            [0x0000] = Hex.Parse(handle),
            [SdpServiceRecord.ServiceClassIdListAttribute] = Hex.Parse("35 11 1C A2 12 9F F3 08 1B 4C 45 8A FE 46 9D 9C 48 42 EC"),
            [SdpServiceRecord.ProtocolDescriptorListAttribute] = Hex.Parse(protocolDescriptorList),
            [0x0006] = Hex.Parse("35 09 09 65 6E 09 00 6A 09 01 00"),
            [SdpServiceRecord.ServiceNameAttribute] = Hex.Parse("25 04 47 41 49 41"),
        });

        Assert.Equal(expectedChannel, record.RfcommChannel);
        Assert.Equal("GAIA", record.ServiceName);
        Assert.Equal(ProtocolConstants.GaiaServiceUuid, SdpParser.Parse(record.RawAttributes[SdpServiceRecord.ServiceClassIdListAttribute]).Children[0].AsUuid());
        Assert.Equal("Sequence[uint:25966, uint:106, uint:256]", record.Describe(0x0006)); // "en", UTF-8, Basis 0x0100
    }

    [Theory]
    [InlineData("35 0C 35 03")] // Sequenz kürzer als angegeben
    [InlineData("F8")] // unbekannter Typ 31
    [InlineData("08 0F 00")] // überzählige Bytes
    [InlineData("")] // leer
    public void Rejects_invalid_data(string hex)
    {
        Assert.Throws<FormatException>(() => SdpParser.Parse(Hex.Parse(hex)));
    }
}
