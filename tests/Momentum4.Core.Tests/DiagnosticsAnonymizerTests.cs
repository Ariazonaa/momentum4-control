// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Core.Diagnostics;

namespace Momentum4.Core.Tests;

public sealed class DiagnosticsAnonymizerTests
{
    [Theory]
    [InlineData("Gerät 80:C3:BA:12:34:56 verbunden", "Gerät 80:C3:BA:xx:xx:xx verbunden")]
    [InlineData("Bluetooth48:45:e6:12:34:56-80:c3:ba:12:34:56#RFCOMM", "Bluetooth48:45:e6:xx:xx:xx-80:c3:ba:xx:xx:xx#RFCOMM")]
    [InlineData("MAC 80-C3-BA-9A-43-92.", "MAC 80-C3-BA-xx-xx-xx.")]
    public void Keeps_only_the_manufacturer_part_of_bluetooth_addresses(string input, string expected)
    {
        Assert.Equal(expected, DiagnosticsAnonymizer.Anonymize(input, []));
    }

    [Fact]
    public void Removes_device_names_from_logged_device_entries()
    {
        const string line = "RX 0495:1501 GetPairedDeviceInfo [01 01 00 4D 61 63 42 6F 6F 6B 20 50 72 6F 20 76 6F 6E 20 52 00] 19 ms OK";

        Assert.Equal("RX 0495:1501 GetPairedDeviceInfo [01 01 00 (Name entfernt)] 19 ms OK", DiagnosticsAnonymizer.Anonymize(line, []));
    }

    [Fact]
    public void Removes_device_names_from_raw_frames()
    {
        const string line = "RX 18 Byte: FF 03 00 0A 04 95 15 01 02 02 00 69 50 68 6F 6E 65 00";

        Assert.Equal("RX 18 Byte: FF 03 00 0A 04 95 15 01 02 02 00 (Name entfernt)", DiagnosticsAnonymizer.Anonymize(line, []));
    }

    [Fact]
    public void Replaces_sensitive_words_longest_first_and_case_insensitive()
    {
        const string text = "#1 Sample Headphones, #2 Tablet, Log in C:\\Users\\user123\\AppData, TABLET";

        var result = DiagnosticsAnonymizer.Anonymize(text, ["Tablet", "Sample Headphones", "user123", "Z", " "]);

        Assert.Equal("#1 <Gerät 1>, #2 <Gerät 3>, Log in C:\\Users\\<Gerät 2>\\AppData, <Gerät 3>", result);
    }

    [Fact]
    public void Leaves_other_hex_and_text_alone()
    {
        const string line = "RX 0495:1B01 GetAncModes [01 01 02 00 03 00] 21 ms OK – Firmware 3.37.3";

        Assert.Equal(line, DiagnosticsAnonymizer.Anonymize(line, []));
    }
}
