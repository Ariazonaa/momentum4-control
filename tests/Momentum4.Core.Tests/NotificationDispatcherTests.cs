// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Device;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Features;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

// Frames aus Hardware-Stufe 5 (FW 3.37.3, tests/CapturedPackets/2026-09-18_fw3.37.3_stage5-notifications.json).
public sealed class NotificationDispatcherTests
{
    private readonly NotificationDispatcher _dispatcher = new(NullLogger<NotificationDispatcher>.Instance);
    private readonly List<RefreshScope> _refreshes = [];

    public NotificationDispatcherTests()
    {
        _dispatcher.RefreshRequested += (_, scope) => _refreshes.Add(scope);
    }

    [Fact]
    public void Maps_dump_notifications_to_events()
    {
        Assert.Equal(new BatteryChanged(100), Translate("FF 03 00 01 04 95 06 83 64"));
        Assert.Equal(new ChargingChanged(false), Translate("FF 03 00 01 04 95 06 82 00"));
        Assert.Equal(new AncEnabledChanged(true), Translate("FF 03 00 01 04 95 1A 85 01"));
        Assert.Equal(new AncLevelChanged(0), Translate("FF 03 00 01 04 95 1A 83 00"));
        Assert.Equal(new BassBoostChanged(true), Translate("FF 03 00 01 04 95 10 89 01"));

        var modes = Assert.IsType<AncModesChanged>(Translate("FF 03 00 06 04 95 1A 81 01 01 02 00 03 00"));
        Assert.Equal((AntiWindMode.Maximum, false), (modes.Table.AntiWind, modes.Table.Adaptive));

        var gains = Assert.IsType<EqGainsChanged>(Translate("FF 03 00 05 04 95 10 82 3C 3C 16 16 00"));
        Assert.Equal([6.0m, 6.0m, 2.2m, 2.2m, 0.0m], gains.GainsDb);

        Assert.Empty(_refreshes);
    }

    [Theory]
    [InlineData("FF 03 00 0F 04 95 10 8B 00 00 5A 01 01 45 02 05 DC 03 19 64 04 19 64")] // Frequenzen
    [InlineData("FF 03 00 0F 04 95 10 8D 00 08 00 01 0B 5C 02 0B 5C 03 0B 5C 04 0B 5C")] // Güte
    [InlineData("FF 03 00 0A 04 95 10 8F 00 0E 01 0E 02 0E 03 0E 04 0E")] // Filtertyp
    [InlineData("FF 03 00 0F 04 95 10 91 00 00 00 01 00 00 02 00 00 03 00 00 04 00 00")]
    [InlineData("FF 03 00 02 04 95 10 93 00 00")]
    [InlineData("FF 03 00 06 04 95 14 8B 00 00 00 00 00 00")] // Bedeutung unbekannt
    [InlineData("FF 03 00 01 04 95 08 80 FF")] // Codec: Stream gestoppt (2026-09-19)
    [InlineData("FF 03 00 04 04 95 08 9A 00 00 BB 80")] // Sample-Rate 48000
    public void Ignores_static_and_unknown_notifications_without_refresh(string hex)
    {
        Assert.Null(Translate(hex));
        Assert.Empty(_refreshes);
    }

    [Fact]
    public void Wear_state_comes_as_notification_or_as_response_dump()
    {
        Assert.Equal(new WearStateChanged(WearState.NotWorn), Translate("FF 03 00 01 04 95 04 82 02"));
        var dump = Assert.Single(new GaiaFrameReader().Push(Hex.Parse("FF 03 00 01 04 95 05 02 03")).Frames);
        Assert.Null(_dispatcher.Translate(dump)); // Response, keine Notification
        Assert.Equal(new WearStateChanged(WearState.Worn), _dispatcher.TranslateUnsolicitedResponse(dump));
        var other = Assert.Single(new GaiaFrameReader().Push(Hex.Parse("FF 03 00 01 04 95 07 03 64")).Frames);
        Assert.Null(_dispatcher.TranslateUnsolicitedResponse(other));
        Assert.Empty(_refreshes);
    }

    [Fact]
    public void Touch_lock_and_sound_mode_notifications_update_the_state()
    {
        // tests/CapturedPackets/2026-09-19_fw3.37.3_touch-wear-podcast.json
        Assert.Equal(new BehaviorSettingChanged(BehaviorSetting.TouchControl, false), Translate("FF 03 00 01 04 95 16 87 01"));
        Assert.Equal(new BehaviorSettingChanged(BehaviorSetting.TouchControl, true), Translate("FF 03 00 01 04 95 16 87 00"));
        Assert.Equal(new SoundModeChanged(SoundMode.Podcast), Translate("FF 03 00 02 04 95 08 84 00 02"));
        Assert.Empty(_refreshes);
    }

    [Fact]
    public void Undecodable_payload_requests_refresh_instead_of_guessing()
    {
        Assert.Null(Translate("FF 03 00 01 04 95 1A 83 65")); // Pegel 101
        Assert.Equal([RefreshScope.NoiseControl], _refreshes);
    }

    [Fact]
    public void Ignores_responses_and_other_vendors()
    {
        Assert.Null(Translate("FF 03 00 01 04 95 07 03 64")); // Response, keine Notification
        Assert.Null(Translate("FF 03 00 01 00 1D 06 83 64")); // Qualcomm-Vendor
    }

    private DeviceEvent? Translate(string hex) =>
        _dispatcher.Translate(Assert.Single(new GaiaFrameReader().Push(Hex.Parse(hex)).Frames));
}
