// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Features;

namespace Momentum4.Protocol.Tests;

public sealed class FeatureCodecTests
{
    [Fact]
    public void Decodes_firmware_version_as_three_big_endian_words()
    {
        // 3.38.3 ist eine Version, die [DS] beobachtet hat.
        Assert.Equal(new FirmwareVersion(3, 38, 3), VersionsCodec.DecodeFirmwareVersion(Hex.Parse("00 03 00 26 00 03")));
    }

    [Theory]
    [InlineData("00 03 00 26")]
    [InlineData("00 03 00 26 00 03 00")]
    [InlineData("")]
    public void Rejects_firmware_version_with_wrong_length(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => VersionsCodec.DecodeFirmwareVersion(Hex.Parse(hex)));
    }

    [Fact]
    public void Compares_firmware_versions()
    {
        Assert.True(new FirmwareVersion(3, 38, 3) > new FirmwareVersion(2, 13, 42));
        Assert.True(new FirmwareVersion(2, 12, 0) <= new FirmwareVersion(2, 12, 0));
        Assert.Equal("2.13.42", new FirmwareVersion(2, 13, 42).ToString());
    }

    [Theory]
    [InlineData("4D 4F 4D 45 4E 54 55 4D 20 34", "MOMENTUM 4")]
    [InlineData("4D 34 00", "M4")]
    [InlineData("4D 34 00 FF FF", "M4")] // alles ab dem ersten Nullbyte wird ignoriert
    public void Decodes_model_id(string hex, string expected)
    {
        Assert.Equal(expected, VersionsCodec.DecodeModelId(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C3 28")] // ungültiges UTF-8
    public void Rejects_invalid_model_id(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => VersionsCodec.DecodeModelId(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData("46", 70)]
    [InlineData("00", 0)]
    [InlineData("64", 100)]
    [InlineData("50 20", 80)] // weitere Bytes (laut m4.json für weitere Geräte) – erster Wert zählt
    public void Decodes_battery_level(string hex, int expected)
    {
        Assert.Equal(expected, BatteryCodec.DecodeLevel(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("65")] // 101 %
    [InlineData("FF")]
    public void Rejects_invalid_battery_level(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => BatteryCodec.DecodeLevel(Hex.Parse(hex)));
    }

    [Fact]
    public void Decodes_gaia_api_version()
    {
        Assert.Equal(new GaiaApiVersion(3, 1), CoreCodec.DecodeApiVersion([0x03, 0x01]));
    }

    [Theory]
    [InlineData("03")]
    [InlineData("03 01 00")]
    public void Rejects_api_version_with_wrong_length(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => CoreCodec.DecodeApiVersion(Hex.Parse(hex)));
    }
}
