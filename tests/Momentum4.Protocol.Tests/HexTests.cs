// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Protocol.Tests;

public sealed class HexTests
{
    [Fact]
    public void Formats_bytes_space_separated_uppercase()
    {
        Assert.Equal("FF 03 00 00 04 95 06 03", Hex.Format([0xFF, 0x03, 0x00, 0x00, 0x04, 0x95, 0x06, 0x03]));
    }

    [Fact]
    public void Formats_empty_as_empty_string()
    {
        Assert.Equal(string.Empty, Hex.Format([]));
    }

    [Theory]
    [InlineData("FF 03 00 0a")]
    [InlineData("ff0300 0A")]
    [InlineData("FF-03-00-0A")]
    [InlineData("FF:03:00:0A")]
    public void Parses_common_notations(string text)
    {
        Assert.Equal(new byte[] { 0xFF, 0x03, 0x00, 0x0A }, Hex.Parse(text));
    }

    [Theory]
    [InlineData("FF 0")] // ungerade Anzahl Ziffern
    [InlineData("FF GG")] // kein Hex
    public void Rejects_invalid_text(string text)
    {
        Assert.Throws<FormatException>(() => Hex.Parse(text));
    }
}
