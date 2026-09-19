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

/// <summary>EQ-Setter (Band, Kurve mit Rollback, Bass Boost) mit Write → Verify gegen das simulierte Headset.</summary>
public sealed class EqualizerActionTests
{
    private const int TestTimeoutMs = 15_000;

    private static readonly CommandPolicy SetterPolicy = CommandPolicy.ForHardwareTest(CommandCatalog.SetEqBand, CommandCatalog.SetBassBoost, CommandCatalog.SetSoundMode);

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();

    public EqualizerActionTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Band_is_written_and_verified()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetEqBandAsync(4, 1.5m, TestContext.Current.CancellationToken);

        Assert.Equal([6.0m, 6.0m, 2.2m, 2.2m, 1.5m], result.Bands.Select(b => b.GainDb));
        Assert.Equal(["FF 03 00 02 04 95 10 01 04 0F"], EqWrites());
        Assert.Equal(1.5m, service.Store.Current.Equalizer!.Bands[4].GainDb);
        Assert.Equal(6500, service.Store.Current.Equalizer!.Bands[4].FrequencyHz); // Frequenzen bleiben erhalten
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Negative_gain_is_encoded_as_signed_byte()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetEqBandAsync(0, -6.0m, TestContext.Current.CancellationToken);

        Assert.Equal(-6.0m, result.Bands[0].GainDb);
        Assert.Equal(["FF 03 00 02 04 95 10 01 00 C4"], EqWrites());
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(5, 0.0)] // nur 5 Bänder
    [InlineData(0, 6.1)] // Bereich aus 0x1000: −6,0 … +6,0 dB
    [InlineData(0, -6.1)]
    [InlineData(0, 0.05)] // nur 0,1-dB-Schritte
    public async Task Invalid_band_or_gain_writes_nothing(int band, double gain)
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetEqBandAsync(band, (decimal)gain, TestContext.Current.CancellationToken));

        Assert.Empty(EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Same_value_writes_nothing()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await service.SetEqBandAsync(2, 2.2m, TestContext.Current.CancellationToken);
        await service.SetEqGainsAsync([6.0m, 6.0m, 2.2m, 2.2m, 0.0m], TestContext.Current.CancellationToken);
        await service.SetBassBoostAsync(true, TestContext.Current.CancellationToken);

        Assert.Empty(EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Curve_writes_only_changed_bands_in_order()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetEqGainsAsync([0.0m, 6.0m, 0.0m, 2.2m, -1.0m], TestContext.Current.CancellationToken);

        Assert.Equal([0.0m, 6.0m, 0.0m, 2.2m, -1.0m], result.Bands.Select(b => b.GainDb));
        Assert.Equal(["FF 03 00 02 04 95 10 01 00 00", "FF 03 00 02 04 95 10 01 02 00", "FF 03 00 02 04 95 10 01 04 F6"], EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Curve_rolls_back_written_bands_in_reverse_order_when_a_band_fails()
    {
        _headset.RejectEqBand = 3;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GaiaErrorException>(() => service.SetEqGainsAsync([0.0m, 0.0m, 0.0m, 0.0m, 0.0m], TestContext.Current.CancellationToken));

        Assert.Equal(
            [
                "FF 03 00 02 04 95 10 01 00 00", "FF 03 00 02 04 95 10 01 01 00", "FF 03 00 02 04 95 10 01 02 00", "FF 03 00 02 04 95 10 01 03 00",
                "FF 03 00 02 04 95 10 01 03 16", "FF 03 00 02 04 95 10 01 02 16", "FF 03 00 02 04 95 10 01 01 3C", "FF 03 00 02 04 95 10 01 00 3C",
            ],
            EqWrites()); // das fehlgeschlagene Band zuerst: bei einem Timeout könnte es trotzdem übernommen worden sein
        Assert.Equal(new byte[] { 0x3C, 0x3C, 0x16, 0x16, 0x00 }, _headset.EqGains); // Ausgangszustand
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Curve_with_wrong_band_count_writes_nothing()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetEqGainsAsync([0.0m, 0.0m], TestContext.Current.CancellationToken));

        Assert.Empty(EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Bass_boost_is_written_and_verified()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetBassBoostAsync(false, TestContext.Current.CancellationToken);

        Assert.False(result.BassBoost);
        Assert.Equal(["FF 03 00 01 04 95 10 08 00"], EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Blind_ack_is_detected_by_read_back()
    {
        _headset.IgnoreWrites = true;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<SettingNotAppliedException<EqualizerState>>(() => service.SetEqBandAsync(4, 3.0m, TestContext.Current.CancellationToken));

        Assert.Equal(0.0m, error.Actual.Bands[4].GainDb);
        Assert.Equal(0.0m, service.Store.Current.Equalizer!.Bands[4].GainDb); // Store zeigt die Wahrheit
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Bass_boost_is_refused_on_firmware_2()
    {
        _headset.Set("FF 03 00 00 04 95 12 01", "FF 03 00 06 04 95 13 01 00 02 00 0D 00 2A"); // FW 2.13.42
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBassBoostAsync(false, TestContext.Current.CancellationToken));

        Assert.Empty(EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_the_eq_setters_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        await service.SetEqBandAsync(4, 1.0m, TestContext.Current.CancellationToken);
        var result = await service.SetBassBoostAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal((1.0m, false), (result.Bands[4].GainDb, result.BassBoost));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Eq_action_needing_a_disallowed_command_writes_nothing()
    {
        var policy = CommandPolicy.Only([.. CommandCatalog.All.Where(d => d.Safety == SafetyClass.Read && d.Status == ProtocolStatus.Verified)]);
        await using var service = await StartAsync(policy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => service.SetEqGainsAsync([0.0m, 0.0m, 0.0m, 0.0m, 0.0m], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CommandNotAllowedException>(() => service.SetBassBoostAsync(false, TestContext.Current.CancellationToken));

        Assert.Empty(EqWrites());
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Sound_mode_is_written_and_verified()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.SetSoundModeAsync(SoundMode.Off, TestContext.Current.CancellationToken);

        Assert.Equal(SoundMode.Off, result.SoundMode);
        Assert.Equal(["FF 03 00 02 04 95 08 03 00 00"], _headset.RequestsStartingWith("FF 03 00 02 04 95 08 03"));
        Assert.Equal(SoundMode.Off, service.Store.Current.Equalizer!.SoundMode);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Sound_personalization_is_refused_without_writing()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetSoundModeAsync(SoundMode.SoundPersonalization, TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 02 04 95 08 03"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_switching_the_sound_mode_back_to_equalizer()
    {
        _headset.SoundMode = 0;
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        var result = await service.SetSoundModeAsync(SoundMode.Equalizer, TestContext.Current.CancellationToken);

        Assert.Equal(SoundMode.Equalizer, result.SoundMode);
        Assert.Equal(["FF 03 00 02 04 95 08 03 00 01"], _headset.RequestsStartingWith("FF 03 00 02 04 95 08 03"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_podcast_mode_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        var result = await service.SetSoundModeAsync(SoundMode.Podcast, TestContext.Current.CancellationToken);

        Assert.Equal(SoundMode.Podcast, result.SoundMode);
        Assert.Equal(["FF 03 00 02 04 95 08 03 00 02"], _headset.RequestsStartingWith("FF 03 00 02 04 95 08 03"));
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
        Assert.NotNull(service.Store.Current.Equalizer);
        return service;
    }

    private List<string> EqWrites() =>
        [.. _headset.Requests.Where(r => r.Contains(" 04 95 10 01", StringComparison.Ordinal) || r.Contains(" 04 95 10 08", StringComparison.Ordinal))];
}
