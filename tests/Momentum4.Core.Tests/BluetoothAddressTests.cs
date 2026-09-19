// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Transport;

namespace Momentum4.Core.Tests;

public sealed class BluetoothAddressTests
{
    [Fact]
    public void Formats_as_colon_separated_hex()
    {
        Assert.Equal("01:23:45:67:89:AB", new BluetoothAddress(0x0123456789AB).ToString());
    }

    [Fact]
    public void Anonymized_form_keeps_only_vendor_part()
    {
        Assert.Equal("01:23:45:xx:xx:xx", new BluetoothAddress(0x0123456789AB).ToAnonymizedString());
    }

    [Theory]
    [InlineData("01:23:45:67:89:AB")]
    [InlineData("01-23-45-67-89-ab")]
    [InlineData("0123456789AB")]
    public void Parses_common_notations(string text)
    {
        Assert.Equal(new BluetoothAddress(0x0123456789AB), BluetoothAddress.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01:23:45:67:89")]
    [InlineData("01:23:45:67:89:AB:CD")]
    [InlineData("01:23:45:67:89:ZZ")]
    public void Rejects_invalid_addresses(string text)
    {
        Assert.Throws<FormatException>(() => BluetoothAddress.Parse(text));
    }
}
