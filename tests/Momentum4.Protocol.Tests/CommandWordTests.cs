// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

public sealed class CommandWordTests
{
    [Fact]
    public void Splits_battery_command_into_feature_type_and_number()
    {
        var word = new CommandWord(0x0603);

        Assert.Equal(3, word.Feature);
        Assert.Equal(PacketType.Command, word.Type);
        Assert.Equal(3, word.Number);
    }

    [Fact]
    public void Derives_response_notification_and_error_ids()
    {
        var word = new CommandWord(0x0603);

        Assert.Equal(new CommandWord(0x0703), word.AsResponse());
        Assert.Equal(new CommandWord(0x0683), word.AsNotification());
        Assert.Equal(new CommandWord(0x0783), word.AsError());
    }

    [Theory]
    [InlineData(0x1B05, 0x1A05)] // Response ANC ein/aus
    [InlineData(0x1A85, 0x1A05)] // Notification ANC ein/aus
    [InlineData(0x1B83, 0x1A03)] // Error Pegel
    [InlineData(0x0100, 0x0000)] // Response Qualcomm-API-Version
    public void Clears_type_bits(int value, int expectedCommand)
    {
        Assert.Equal(new CommandWord((ushort)expectedCommand), new CommandWord((ushort)value).AsCommand());
    }

    [Fact]
    public void Creates_word_from_parts()
    {
        Assert.Equal(new CommandWord(0x1B05), CommandWord.Create(13, PacketType.Response, 5));
    }

    [Fact]
    public void Formats_as_hex()
    {
        Assert.Equal("0x1A05", new CommandWord(0x1A05).ToString());
    }
}
