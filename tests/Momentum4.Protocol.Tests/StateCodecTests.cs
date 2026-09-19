// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Features;

namespace Momentum4.Protocol.Tests;

// Vektoren aus den Referenzen ([MC]-Tests, [DS]-Beobachtungen) bzw. nach dem dokumentierten Schema gebaut.
public sealed class StateCodecTests
{
    [Theory]
    [InlineData("00", false)]
    [InlineData("01", true)]
    public void Decodes_switches(string hex, bool expected)
    {
        Assert.Equal(expected, SwitchCodec.Decode(Hex.Parse(hex), "Test"));
    }

    [Theory]
    [InlineData("02")]
    [InlineData("")]
    [InlineData("01 00")]
    public void Rejects_invalid_switch(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => SwitchCodec.Decode(Hex.Parse(hex), "Test"));
    }

    [Fact]
    public void Decodes_anc_mode_table_like_reference_test()
    {
        // [MC]-Test: Adaptive an, Anti-Wind automatisch, Comfort aus
        var table = AncCodec.DecodeModes(Hex.Parse("03 01 01 02 02 00"));

        Assert.Equal(AntiWindMode.Automatic, table.AntiWind);
        Assert.Equal((byte)0, table.Comfort);
        Assert.True(table.Adaptive);
        Assert.Equal(3, table.Pairs.Count);
    }

    [Fact]
    public void Keeps_unknown_anc_mode_ids()
    {
        var table = AncCodec.DecodeModes(Hex.Parse("01 00 02 00 03 00 07 09"));

        Assert.Equal(AntiWindMode.Off, table.AntiWind);
        Assert.False(table.Adaptive);
        Assert.Contains(((byte)7, (byte)9), table.Pairs);
    }

    [Theory]
    [InlineData("01")] // ungerade
    [InlineData("")] // leer
    [InlineData("01 03")] // Anti-Wind 3 unbekannt
    [InlineData("03 02")] // Adaptive 2 unbekannt
    public void Rejects_invalid_anc_mode_table(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => AncCodec.DecodeModes(Hex.Parse(hex)));
    }

    [Fact]
    public void Encodes_anc_mode_as_full_table_keeping_other_values()
    {
        var table = AncCodec.DecodeModes(Hex.Parse("01 01 02 00 03 00"));

        Assert.Equal("01 02 02 00 03 00", Hex.Format(AncCodec.EncodeMode(table, AncCodec.ModeAntiWind, 2, AncModeWriteFormat.FullTable)));
        Assert.Equal("01 01 02 00 03 01", Hex.Format(AncCodec.EncodeMode(table, AncCodec.ModeAdaptive, 1, AncModeWriteFormat.FullTable)));
    }

    [Fact]
    public void Encodes_anc_mode_as_single_pair()
    {
        var table = AncCodec.DecodeModes(Hex.Parse("01 01 02 00 03 00"));

        Assert.Equal("03 01", Hex.Format(AncCodec.EncodeMode(table, AncCodec.ModeAdaptive, 1, AncModeWriteFormat.SinglePair)));
    }

    [Fact]
    public void Refuses_invalid_values()
    {
        var table = AncCodec.DecodeModes(Hex.Parse("01 01 02 00 03 00"));

        Assert.Equal("01 01 02 01 03 00", Hex.Format(AncCodec.EncodeMode(table, AncCodec.ModeComfort, 1, AncModeWriteFormat.FullTable)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AncCodec.EncodeMode(table, AncCodec.ModeComfort, 2, AncModeWriteFormat.SinglePair));
        Assert.Throws<ArgumentOutOfRangeException>(() => AncCodec.EncodeMode(table, AncCodec.ModeAntiWind, 3, AncModeWriteFormat.SinglePair));
        Assert.Throws<ArgumentOutOfRangeException>(() => AncCodec.EncodeMode(table, AncCodec.ModeAdaptive, 2, AncModeWriteFormat.SinglePair));
        Assert.Throws<ArgumentOutOfRangeException>(() => AncCodec.EncodeLevel(101));
    }

    [Theory]
    [InlineData("00", 0)]
    [InlineData("64", 100)]
    public void Decodes_anc_level(string hex, int expected)
    {
        Assert.Equal(expected, AncCodec.DecodeLevel(Hex.Parse(hex)));
    }

    [Fact]
    public void Rejects_anc_level_above_100()
    {
        Assert.Throws<ProtocolFormatException>(() => AncCodec.DecodeLevel([0x65]));
    }

    [Theory]
    [InlineData("00 00", SoundMode.Off)] // [MC] CHANGELOG
    [InlineData("00 01", SoundMode.Equalizer)]
    [InlineData("00 02", SoundMode.Podcast)] // [MC] CHANGELOG
    [InlineData("00 03", SoundMode.SoundPersonalization)]
    public void Decodes_sound_mode(string hex, SoundMode expected)
    {
        Assert.Equal(expected, GenericAudioCodec.DecodeSoundMode(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("01 01")]
    [InlineData("00 04")]
    public void Rejects_invalid_sound_mode(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => GenericAudioCodec.DecodeSoundMode(Hex.Parse(hex)));
    }

    [Fact]
    public void Decodes_eq_config_like_reference_test()
    {
        // [MC]-Test: 5 Bänder, −6,0 … +6,0 dB
        Assert.Equal(new EqConfig(5, -6.0m, 6.0m), UserEqCodec.DecodeConfig(Hex.Parse("05 C4 3C")));
    }

    [Theory]
    [InlineData("DF", -3.3)] // [MC]: −3,25 dB wird als 0xDF kodiert
    [InlineData("14", 2.0)] // [DS]: Pop-Preset Band 1 = 20
    [InlineData("01 EC", -2.0)] // mit Band-Echo
    public void Decodes_eq_band_gain(string hex, double expectedDb)
    {
        Assert.Equal((decimal)expectedDb, UserEqCodec.DecodeBandGain(Hex.Parse(hex), band: 1));
    }

    [Fact]
    public void Rejects_eq_band_with_wrong_echo()
    {
        Assert.Throws<ProtocolFormatException>(() => UserEqCodec.DecodeBandGain(Hex.Parse("02 EC"), band: 1));
    }

    [Fact]
    public void Decodes_all_eq_gains()
    {
        // Eigenes Gerät, FW 3.37.3
        Assert.Equal(new[] { 6.0m, 6.0m, 2.2m, 2.2m, 0.0m }, UserEqCodec.DecodeAllGains(Hex.Parse("3C 3C 16 16 00"), 5));
        Assert.Throws<ProtocolFormatException>(() => UserEqCodec.DecodeAllGains(Hex.Parse("3C 3C 16 16"), 5));
    }

    [Theory]
    [InlineData("00 00 5A", 0, 90)]
    [InlineData("01 01 45", 1, 325)]
    [InlineData("02 05 DC", 2, 1500)]
    [InlineData("04 19 64", 4, 6500)]
    public void Decodes_eq_band_frequency(string hex, int band, int expectedHz)
    {
        // Eigenes Gerät, FW 3.37.3; Werte wie von [DS] beschrieben
        Assert.Equal(expectedHz, UserEqCodec.DecodeBandFrequency(Hex.Parse(hex), band));
    }

    [Fact]
    public void Rejects_eq_band_frequency_with_wrong_echo()
    {
        Assert.Throws<ProtocolFormatException>(() => UserEqCodec.DecodeBandFrequency(Hex.Parse("01 01 45"), 0));
    }

    [Fact]
    public void Keeps_extra_eq_config_bytes_seen_on_firmware_3_37_3()
    {
        var config = UserEqCodec.DecodeConfig(Hex.Parse("05 C4 3C 00 00"));

        Assert.Equal(new EqConfig(5, -6.0m, 6.0m, [0x00, 0x00]), config);
    }

    [Theory]
    [InlineData("00 00")] // keine Bänder
    [InlineData("05 3C C4")] // min > max
    [InlineData("05 C4")] // zu kurz
    public void Rejects_invalid_eq_config(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => UserEqCodec.DecodeConfig(Hex.Parse(hex)));
    }

    [Fact]
    public void Decodes_paired_device_entry_like_reference_test()
    {
        // [MC]-Test: Name "Office PC" mit Nullbyte; [DS]: "<index> 01 01 <name…> 00"
        var entry = DeviceManagementCodec.DecodeEntry(Hex.Parse("02 01 01 4F 66 66 69 63 65 20 50 43 00"), expectedIndex: 2);

        Assert.Equal(new PairedDeviceEntry(2, 1, 1, "Office PC"), entry);
        Assert.True(entry.IsConnected);
    }

    [Fact]
    public void Rejects_paired_device_entry_with_wrong_index_echo()
    {
        Assert.Throws<ProtocolFormatException>(() => DeviceManagementCodec.DecodeEntry(Hex.Parse("01 01 01 41 00"), expectedIndex: 0));
    }

    [Theory]
    [InlineData("00 03", 3)]
    [InlineData("00 00", 0)]
    public void Decodes_paired_device_count(string hex, int expected)
    {
        Assert.Equal(expected, DeviceManagementCodec.DecodeCount(Hex.Parse(hex)));
    }

    [Fact]
    public void Rejects_implausible_paired_device_count()
    {
        Assert.Throws<ProtocolFormatException>(() => DeviceManagementCodec.DecodeCount(Hex.Parse("01 00")));
    }

    [Fact]
    public void Decodes_auto_power_off_timer_like_observation()
    {
        // [DS]: 0x0601 [00] → 00 <sec:u16>, 3600 s bei „60 min“
        Assert.Equal(3600, BatteryCodec.DecodeTimerSeconds(Hex.Parse("00 0E 10"), BatteryCodec.TimerAutoPowerOff));
    }

    [Fact]
    public void Rejects_timer_with_wrong_echo()
    {
        Assert.Throws<ProtocolFormatException>(() => BatteryCodec.DecodeTimerSeconds(Hex.Parse("01 0E 10"), BatteryCodec.TimerAutoPowerOff));
    }

    [Theory]
    [InlineData("00", BtCompatibilityMode.BetterAudio)]
    [InlineData("01", BtCompatibilityMode.BetterCompatibility)]
    public void Decodes_bt_compatibility_mode(string hex, BtCompatibilityMode expected)
    {
        Assert.Equal(expected, DeviceCodec.DecodeBtCompatibilityMode(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData("02", PersonalizationState.Calibrated)]
    [InlineData("00", PersonalizationState.NotParameterized)]
    public void Decodes_personalization_state(string hex, PersonalizationState expected)
    {
        Assert.Equal(expected, DeviceCodec.DecodePersonalizationState(Hex.Parse(hex)));
    }

    [Theory]
    [InlineData(4, 1.5, "04 0F")]
    [InlineData(0, -6.0, "00 C4")]
    [InlineData(1, 12.7, "01 7F")]
    [InlineData(2, -12.8, "02 80")]
    [InlineData(3, 0.0, "03 00")]
    public void Encodes_eq_band_gain_in_tenths_of_db(int band, double gainDb, string expected)
    {
        Assert.Equal(expected, Hex.Format(UserEqCodec.EncodeBandGain(band, (decimal)gainDb)));
    }

    [Theory]
    [InlineData(0, 0.05)]
    [InlineData(0, 12.8)]
    [InlineData(0, -12.9)]
    [InlineData(-1, 0.0)]
    [InlineData(256, 0.0)]
    public void Rejects_unencodable_eq_band_gain(int band, double gainDb)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserEqCodec.EncodeBandGain(band, (decimal)gainDb));
    }

    [Theory]
    [InlineData(true, "01")]
    [InlineData(false, "00")]
    public void Encodes_bass_boost(bool enabled, string expected)
    {
        Assert.Equal(expected, Hex.Format(UserEqCodec.EncodeBassBoost(enabled)));
    }

    [Theory]
    [InlineData(SoundMode.Off, "00 00")]
    [InlineData(SoundMode.Equalizer, "00 01")]
    [InlineData(SoundMode.Podcast, "00 02")]
    public void Encodes_sound_mode(SoundMode mode, string expected)
    {
        Assert.Equal(expected, Hex.Format(GenericAudioCodec.EncodeSoundMode(mode)));
    }

    [Fact]
    public void Refuses_to_encode_unknown_sound_mode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GenericAudioCodec.EncodeSoundMode((SoundMode)4));
    }

    [Theory]
    [InlineData("02 01", 2, true)]
    [InlineData("02 00", 2, false)]
    [InlineData("01 02", 1, true)] // status ≠ 0 = verbunden ([MC])
    public void Decodes_peer_connection_status(string hex, int index, bool expected)
    {
        Assert.Equal(expected, DeviceManagementCodec.DecodeConnectionStatus(Hex.Parse(hex), index));
    }

    [Theory]
    [InlineData("03 01")] // falsches Index-Echo
    [InlineData("02")]
    [InlineData("02 01 00")]
    public void Rejects_invalid_peer_connection_status(string hex)
    {
        Assert.Throws<ProtocolFormatException>(() => DeviceManagementCodec.DecodeConnectionStatus(Hex.Parse(hex), 2));
    }

    [Fact]
    public void Encodes_peer_index_within_the_sane_range()
    {
        Assert.Equal("02", Hex.Format(DeviceManagementCodec.EncodeIndex(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceManagementCodec.EncodeIndex(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceManagementCodec.EncodeIndex(DeviceManagementCodec.MaxSaneCount));
    }
}
