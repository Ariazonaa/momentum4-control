// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

/// <summary>Deutungen für den Protokoll-Inspektor, geprüft an Frames aus den eigenen Mitschnitten (FW 3.37.3).</summary>
public sealed class FrameDescriberTests
{
    [Theory]
    [InlineData("FF 03 00 01 04 95 07 03 5A", "Akku 90 %")]
    [InlineData("FF 03 00 01 04 95 06 83 64", "Akku 100 %")] // Notification
    [InlineData("FF 03 00 01 04 95 1B 05 01", "ANC an")]
    [InlineData("FF 03 00 01 04 95 1A 85 00", "ANC aus")]
    [InlineData("FF 03 00 06 04 95 1B 01 01 01 02 00 03 00", "Anti-Wind Maximum, Adaptive aus")]
    [InlineData("FF 03 00 01 04 95 1A 83 1E", "Pegel 30 (0 = stärkstes ANC)")]
    [InlineData("FF 03 00 01 04 95 19 05 01", "Transparent Hearing an")]
    [InlineData("FF 03 00 05 04 95 11 03 3C 3C 16 16 00", "EQ +6.0 / +6.0 / +2.2 / +2.2 / 0.0 dB")]
    [InlineData("FF 03 00 05 04 95 10 82 E2 F6 19 14 00", "EQ -3.0 / -1.0 / +2.5 / +2.0 / 0.0 dB")]
    [InlineData("FF 03 00 01 04 95 10 89 00", "Bass Boost aus")]
    [InlineData("FF 03 00 02 04 95 09 04 00 01", "Sound-Mode Equalizer")]
    [InlineData("FF 03 00 06 04 95 13 01 00 03 00 25 00 03", "Firmware 3.37.3")]
    [InlineData("FF 03 00 02 04 95 15 04 02 01", "Gerät #2 verbunden")]
    [InlineData("FF 03 00 02 04 95 14 84 02 00", "Gerät #2 nicht verbunden")]
    [InlineData("FF 03 00 0A 04 95 15 01 02 02 00 50 68 6F 6E 65 2D 43 00", "Gerät #2 „Phone-C“")]
    [InlineData("FF 03 00 00 04 95 1B 04", "bestätigt")]
    [InlineData("FF 03 00 01 04 95 1B 80 05", "abgelehnt, Reason 0x05")]
    [InlineData("FF 03 00 02 00 1D 01 00 03 01", "GAIA-API 3.1")]
    public void Describes_verified_frames(string hex, string expected)
    {
        Assert.Equal(expected, FrameDescriber.Describe(Parse(hex)));
    }

    [Theory]
    [InlineData("FF 03 00 00 04 95 1A 05")] // Request
    [InlineData("FF 03 00 01 04 95 15 0B 00")] // unbekannte Antwort mit Daten
    public void Returns_null_for_requests_and_unknown_data(string hex)
    {
        Assert.Null(FrameDescriber.Describe(Parse(hex)));
    }

    [Fact]
    public void Reports_undecodable_payload_instead_of_guessing()
    {
        Assert.StartsWith("nicht dekodierbar", FrameDescriber.Describe(Parse("FF 03 00 01 04 95 07 03 FF")));
    }

    private static GaiaFrame Parse(string hex) => new GaiaFrameReader().Push(Hex.Parse(hex)).Frames.Single();
}
