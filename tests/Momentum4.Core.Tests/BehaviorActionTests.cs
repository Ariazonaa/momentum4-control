// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Device;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

/// <summary>On-Head-Erkennung, Smart Pause, Auto-Answer, Comfort Call, Touch und Tragezustand: Lesen beim Refresh, Write → Verify.</summary>
public sealed class BehaviorActionTests
{
    private const int TestTimeoutMs = 15_000;

    private static readonly CommandPolicy SetterPolicy = CommandPolicy.ForHardwareTest(
        CommandCatalog.SetOnHeadDetection, CommandCatalog.SetSmartPause, CommandCatalog.SetAutoAnswer, CommandCatalog.SetComfortCall,
        CommandCatalog.SetTouchLock, CommandCatalog.GetPhysicalState, CommandCatalog.SetTimer, CommandCatalog.SetAudioPromptMode);

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();

    public BehaviorActionTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Full_refresh_reads_all_four_settings()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        Assert.Equal(new BehaviorState(OnHeadDetection: true, SmartPause: false, AutoAnswer: false, ComfortCall: true, TouchControl: true), service.Store.Current.Behavior);
        Assert.Equal(WearState.Worn, service.Store.Current.Wear);
        Assert.Empty(_headset.Unanswered);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(BehaviorSetting.OnHeadDetection, false, "FF 03 00 01 04 95 04 00 00")]
    [InlineData(BehaviorSetting.SmartPause, true, "FF 03 00 01 04 95 08 0C 01")]
    [InlineData(BehaviorSetting.AutoAnswer, true, "FF 03 00 01 04 95 08 0A 01")]
    [InlineData(BehaviorSetting.ComfortCall, false, "FF 03 00 01 04 95 08 14 00")]
    [InlineData(BehaviorSetting.TouchControl, false, "FF 03 00 01 04 95 16 06 01")] // Sperre an = Touch aus
    public async Task Setting_is_written_and_verified(BehaviorSetting setting, bool on, string expectedWrite)
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetBehaviorAsync(setting, on, TestContext.Current.CancellationToken);

        Assert.Equal(on, result.Get(setting));
        Assert.Equal([expectedWrite], Writes());
        Assert.Equal(result, service.Store.Current.Behavior);
        foreach (var other in Enum.GetValues<BehaviorSetting>().Where(s => s != setting))
        {
            Assert.Equal(service.Store.Current.Behavior!.Get(other), result.Get(other));
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Same_value_writes_nothing()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await service.SetBehaviorAsync(BehaviorSetting.OnHeadDetection, true, TestContext.Current.CancellationToken);
        await service.SetBehaviorAsync(BehaviorSetting.SmartPause, false, TestContext.Current.CancellationToken);

        Assert.Empty(Writes());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Blind_ack_is_reported_as_not_applied()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        _headset.IgnoreWrites = true;

        var ex = await Assert.ThrowsAsync<SettingNotAppliedException<BehaviorState>>(
            () => service.SetBehaviorAsync(BehaviorSetting.SmartPause, true, TestContext.Current.CancellationToken));

        Assert.False(ex.Actual.SmartPause);
        Assert.False(service.Store.Current.Behavior!.SmartPause);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(1800, "FF 03 00 03 04 95 06 00 00 07 08")]
    [InlineData(3600, "FF 03 00 03 04 95 06 00 00 0E 10")]
    [InlineData(0, "FF 03 00 03 04 95 06 00 00 00 00")]
    public async Task Auto_power_off_is_written_and_verified(int seconds, string expectedWrite)
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        Assert.Equal(900, service.Store.Current.AutoPowerOffSeconds);

        var result = await service.SetAutoPowerOffAsync(seconds, TestContext.Current.CancellationToken);

        Assert.Equal(seconds, result);
        Assert.Equal(seconds, service.Store.Current.AutoPowerOffSeconds);
        Assert.Equal([expectedWrite], _headset.RequestsStartingWith("FF 03 00 03 04 95 06 00"));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(600)]
    [InlineData(-1)]
    [InlineData(7200)]
    public async Task Auto_power_off_outside_the_known_steps_sends_nothing(int seconds)
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetAutoPowerOffAsync(seconds, TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 03 04 95 06 00"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Auto_power_off_blind_ack_is_reported()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        _headset.IgnoreWrites = true;

        var ex = await Assert.ThrowsAsync<SettingNotAppliedException<int>>(() => service.SetAutoPowerOffAsync(1800, TestContext.Current.CancellationToken));

        Assert.Equal(900, ex.Actual);
        Assert.Equal(900, service.Store.Current.AutoPowerOffSeconds);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_auto_power_off_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        Assert.Equal(1800, await service.SetAutoPowerOffAsync(1800, TestContext.Current.CancellationToken));
        Assert.Equal(900, await service.SetAutoPowerOffAsync(900, TestContext.Current.CancellationToken));
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(AudioPromptMode.TonesOnly, "FF 03 00 01 04 95 08 01 01")]
    [InlineData(AudioPromptMode.Off, "FF 03 00 01 04 95 08 01 00")]
    public async Task Prompt_mode_is_written_and_verified(AudioPromptMode mode, string expectedWrite)
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        Assert.Equal(AudioPromptMode.TonesAndVoice, service.Store.Current.PromptMode);

        Assert.Equal(mode, await service.SetAudioPromptModeAsync(mode, TestContext.Current.CancellationToken));

        Assert.Equal([expectedWrite], _headset.RequestsStartingWith("FF 03 00 01 04 95 08 01"));
        Assert.Equal(mode, service.Store.Current.PromptMode);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_prompt_mode_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        Assert.Equal(AudioPromptMode.TonesOnly, await service.SetAudioPromptModeAsync(AudioPromptMode.TonesOnly, TestContext.Current.CancellationToken));
        Assert.Equal(AudioPromptMode.TonesAndVoice, await service.SetAudioPromptModeAsync(AudioPromptMode.TonesAndVoice, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Touch_lock_is_shown_inverted()
    {
        _headset.TouchLocked = true;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        Assert.False(service.Store.Current.Behavior!.TouchControl);
        var result = await service.SetBehaviorAsync(BehaviorSetting.TouchControl, true, TestContext.Current.CancellationToken);

        Assert.True(result.TouchControl);
        Assert.False(_headset.TouchLocked);
        Assert.Equal(["FF 03 00 01 04 95 16 06 00"], Writes());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Wear_state_arrives_by_notification_and_by_the_response_dump_after_registration()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 04 82 02")); // abgesetzt
        await Until(() => service.Store.Current.Wear == WearState.NotWorn);
        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 05 02 03")); // Response ohne Request, wie nach 0x0007 [02]
        await Until(() => service.Store.Current.Wear == WearState.Worn);

        Assert.Equal(WearState.Worn, service.Store.Current.Wear);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Change_on_the_phone_arrives_with_the_next_full_refresh()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        _headset.SmartPause = true;
        service.RequestRefresh(RefreshScope.All);
        for (var i = 0; i < 500 && service.Store.Current.Behavior?.SmartPause != true; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(service.Store.Current.Behavior!.SmartPause);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_all_setters_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        foreach (var setting in Enum.GetValues<BehaviorSetting>())
        {
            var original = service.Store.Current.Behavior!.Get(setting);
            Assert.Equal(!original, (await service.SetBehaviorAsync(setting, !original, TestContext.Current.CancellationToken)).Get(setting));
            Assert.Equal(original, (await service.SetBehaviorAsync(setting, original, TestContext.Current.CancellationToken)).Get(setting));
        }

        Assert.Equal(10, Writes().Count);
    }

    private async Task<Momentum4Service> StartAsync(CommandPolicy policy, CancellationToken cancellationToken)
    {
        var service = new Momentum4Service(_transport, policy, new Momentum4ServiceOptions { RegisterNotifications = false }, NullLoggerFactory.Instance, _time);
        service.Start();
        for (var i = 0; i < 1000 && service.Store.Current.Connection != ConnectionState.Connected; i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.NotNull(service.Store.Current.Behavior);
        return service;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private List<string> Writes() =>
        [.. _headset.Requests.Where(r => r.StartsWith("FF 03 00 01 04 95 04 00", StringComparison.Ordinal)
            || r.StartsWith("FF 03 00 01 04 95 08 0C", StringComparison.Ordinal)
            || r.StartsWith("FF 03 00 01 04 95 08 0A", StringComparison.Ordinal)
            || r.StartsWith("FF 03 00 01 04 95 08 14", StringComparison.Ordinal)
            || r.StartsWith("FF 03 00 01 04 95 16 06", StringComparison.Ordinal))];
}
