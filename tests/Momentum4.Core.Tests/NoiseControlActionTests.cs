// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

/// <summary>Noise-Control-Setter mit Write → Verify gegen das zustandsbehaftete simulierte Headset.</summary>
public sealed class NoiseControlActionTests
{
    private const int TestTimeoutMs = 15_000;

    private static readonly CommandPolicy SetterPolicy = CommandPolicy.ForHardwareTest(
        CommandCatalog.SetAncMode, CommandCatalog.SetAncLevel, CommandCatalog.SetAncEnabled, CommandCatalog.SetTransparentHearingStatus);

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();

    public NoiseControlActionTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Anti_wind_writes_full_table_by_default()
    {
        _headset.TransparentHearing = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetAntiWindAsync(AntiWindMode.Automatic, TestContext.Current.CancellationToken);

        Assert.Equal(AntiWindMode.Automatic, result.AntiWind);
        Assert.Equal(["FF 03 00 06 04 95 1A 00 01 02 02 00 03 00"], _headset.RequestsStartingWith("FF 03 00 06 04 95 1A 00"));
        Assert.Equal(AntiWindMode.Automatic, service.Store.Current.NoiseControl!.AntiWind);
        Assert.Equal(AncModeWriteFormat.FullTable, service.AncWriteFormat);
        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Anti_wind_keeps_transparency_on_although_the_write_ends_it()
    {
        // Hardware-Befund Phase E: 0x1A00 beendet Transparent Hearing – der Service schaltet es danach wieder ein.
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetAntiWindAsync(AntiWindMode.Automatic, TestContext.Current.CancellationToken);

        Assert.Equal((AntiWindMode.Automatic, NoiseControlMode.Transparency, 100), (result.AntiWind, result.Mode, result.Level));
        Assert.Equal(["FF 03 00 01 04 95 18 04 01"], _headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Transparency_switches_transparent_hearing_on()
    {
        _headset.TransparentHearing = false;
        _headset.AncLevel = 0;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetNoiseControlModeAsync(NoiseControlMode.Transparency, TestContext.Current.CancellationToken);

        Assert.Equal((NoiseControlMode.Transparency, 100), (result.Mode, result.Level));
        Assert.Equal(["FF 03 00 01 04 95 18 04 01"], _headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 06 04 95 1A 00"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Transparent_hearing_off_restores_the_level_from_before()
    {
        // Hardware-Befund (01:46): Pegel 30 → 0x1804 [01] → 100 → 0x1804 [00] → wieder 30.
        _headset.TransparentHearing = false;
        _headset.AncLevel = 30;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        Assert.Equal(100, (await service.SetNoiseControlModeAsync(NoiseControlMode.Transparency, TestContext.Current.CancellationToken)).Level);
        var result = await service.SetTransparentHearingAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal((NoiseControlMode.Custom, 30), (result.Mode, result.Level));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Transparent_hearing_off_alone_leaves_custom_with_level_zero()
    {
        // Hardware-Befund 00:41: Transparent Hearing war aus Pegel 0 eingeschaltet worden, 0x1804 [00] ergab wieder 0.
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetTransparentHearingAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal((NoiseControlMode.Custom, 0), (result.Mode, result.Level));
        Assert.Equal(["FF 03 00 01 04 95 18 04 00"], NoiseControlWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Falls_back_to_single_pair_when_full_table_is_rejected_as_malformed()
    {
        _headset.RejectFullTable = true;
        _headset.TransparentHearing = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await service.SetAntiWindAsync(AntiWindMode.Off, TestContext.Current.CancellationToken);
        await service.SetAntiWindAsync(AntiWindMode.Maximum, TestContext.Current.CancellationToken);

        Assert.Equal(AncModeWriteFormat.SinglePair, service.AncWriteFormat);
        Assert.Equal(["FF 03 00 02 04 95 1A 00 01 00", "FF 03 00 02 04 95 1A 00 01 01"], _headset.RequestsStartingWith("FF 03 00 02 04 95 1A 00"));
        Assert.Single(_headset.RequestsStartingWith("FF 03 00 06 04 95 1A 00")); // nur der erste, abgelehnte Versuch
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Adaptive_turns_transparent_hearing_off_and_sets_adaptive_like_reference()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetNoiseControlModeAsync(NoiseControlMode.Adaptive, TestContext.Current.CancellationToken);

        Assert.Equal(NoiseControlMode.Adaptive, result.Mode);
        Assert.False(result.TransparentHearing);
        var writes = _headset.Requests.Where(r => r.Contains(" 04 95 18 04", StringComparison.Ordinal) || r.Contains(" 04 95 1A 04", StringComparison.Ordinal) || r.Contains(" 04 95 1A 00", StringComparison.Ordinal)).ToList();
        Assert.Equal(["FF 03 00 01 04 95 18 04 00", "FF 03 00 06 04 95 1A 00 01 01 02 00 03 01"], writes); // ANC war schon an
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Off_only_switches_anc_off_even_from_transparency()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetNoiseControlModeAsync(NoiseControlMode.Off, TestContext.Current.CancellationToken);

        Assert.Equal(NoiseControlMode.Off, result.Mode);
        Assert.True(result.TransparentHearing); // bleibt formal an, wirkt ohne ANC aber nicht (Hardware-Befund)
        Assert.Equal(["FF 03 00 01 04 95 1A 04 00"], NoiseControlWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Transparency_from_off_switches_anc_on_first_then_transparent_hearing()
    {
        // Hardware-Befund: ANC einschalten beendet Transparent Hearing – es muss danach neu eingeschaltet werden.
        _headset.AncEnabled = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        Assert.Equal(NoiseControlMode.Off, service.Store.Current.NoiseControl!.Mode);

        var result = await service.SetNoiseControlModeAsync(NoiseControlMode.Transparency, TestContext.Current.CancellationToken);

        Assert.Equal((NoiseControlMode.Transparency, 100), (result.Mode, result.Level));
        Assert.Equal(["FF 03 00 01 04 95 1A 04 01", "FF 03 00 01 04 95 18 04 01"], NoiseControlWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Adaptive_from_off_does_not_write_transparent_hearing_off_first()
    {
        // ANC aus, Transparent Hearing formal an: ANC einschalten beendet es ohnehin (Hardware-Befund), 0x1804 [00] ist unnötig.
        _headset.AncEnabled = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetNoiseControlModeAsync(NoiseControlMode.Adaptive, TestContext.Current.CancellationToken);

        Assert.Equal(NoiseControlMode.Adaptive, result.Mode);
        Assert.Equal(["FF 03 00 01 04 95 1A 04 01", "FF 03 00 06 04 95 1A 00 01 01 02 00 03 01"], NoiseControlWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Level_switches_to_custom_and_writes_level()
    {
        _headset.AncTable = [0x01, 0x01, 0x02, 0x00, 0x03, 0x01]; // Adaptive
        _headset.TransparentHearing = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetNoiseControlLevelAsync(40, TestContext.Current.CancellationToken);

        Assert.Equal((NoiseControlMode.Custom, 40), (result.Mode, result.Level));
        Assert.Equal(["FF 03 00 01 04 95 1A 02 28"], _headset.RequestsStartingWith("FF 03 00 01 04 95 1A 02"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Same_value_writes_nothing()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await service.SetAntiWindAsync(AntiWindMode.Maximum, TestContext.Current.CancellationToken);

        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 06 04 95 1A 00"));
        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 02 04 95 1A 00"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Blind_ack_is_detected_by_read_back()
    {
        _headset.IgnoreWrites = true;
        _headset.TransparentHearing = false;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<SettingNotAppliedException<NoiseControlState>>(() => service.SetAntiWindAsync(AntiWindMode.Off, TestContext.Current.CancellationToken));

        Assert.Equal(AntiWindMode.Maximum, error.Actual.AntiWind);
        Assert.Equal(AntiWindMode.Maximum, service.Store.Current.NoiseControl!.AntiWind); // Store zeigt die Wahrheit
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Action_needing_a_disallowed_command_writes_nothing_at_all()
    {
        // Adaptive aus der Transparenz braucht 0x1804 und 0x1A00. Erlaubt ist hier nur 0x1804 – ohne Vorabprüfung wäre
        // Transparent Hearing schon aus, bevor 0x1A00 abgelehnt wird.
        var policy = CommandPolicy.Only(
            [.. CommandCatalog.All.Where(d => d.Safety == SafetyClass.Read && d.Status == ProtocolStatus.Verified), CommandCatalog.SetTransparentHearingStatus]);
        await using var service = await StartAsync(policy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => service.SetNoiseControlModeAsync(NoiseControlMode.Adaptive, TestContext.Current.CancellationToken));

        Assert.Empty(NoiseControlWrites());
        Assert.True(_headset.TransparentHearing); // unverändert
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_the_setters_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        var result = await service.SetAntiWindAsync(AntiWindMode.Automatic, TestContext.Current.CancellationToken);

        Assert.Equal((AntiWindMode.Automatic, NoiseControlMode.Transparency), (result.AntiWind, result.Mode));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_every_noise_control_mode()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        foreach (var mode in new[] { NoiseControlMode.Off, NoiseControlMode.Transparency, NoiseControlMode.Adaptive, NoiseControlMode.Custom, NoiseControlMode.Transparency })
        {
            var result = await service.SetNoiseControlModeAsync(mode, TestContext.Current.CancellationToken);
            Assert.Equal(mode, result.Mode);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Refuses_actions_when_not_connected()
    {
        await using var service = new Momentum4Service(_transport, SetterPolicy, new Momentum4ServiceOptions(), NullLoggerFactory.Instance, _time);

        await Assert.ThrowsAsync<HeadsetUnavailableException>(() => service.SetAntiWindAsync(AntiWindMode.Off, TestContext.Current.CancellationToken));
        Assert.Empty(_headset.Requests);
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
        return service;
    }

    private List<string> NoiseControlWrites() =>
        [.. _headset.Requests.Where(r => r.Contains(" 04 95 18 04", StringComparison.Ordinal) || r.Contains(" 04 95 1A 04", StringComparison.Ordinal) || r.Contains(" 04 95 1A 00", StringComparison.Ordinal))];
}
