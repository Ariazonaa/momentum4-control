// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

/// <summary>Service gegen ein simuliertes Headset, das die echten Mitschnitte (FW 3.37.3) abspielt.</summary>
public sealed class Momentum4ServiceTests
{
    private const int TestTimeoutMs = 15_000;

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();

    public Momentum4ServiceTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Reads_complete_state_like_the_real_headset()
    {
        await using var service = Create(CommandPolicy.Production);

        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        var s = service.Store.Current;
        Assert.Equal(new DeviceInfo("M4AEBT Black", new FirmwareVersion(3, 37, 3)), s.Device);
        Assert.Equal(100, s.BatteryPercent);
        Assert.Equal(new NoiseControlState(true, false, 100, AntiWindMode.Maximum, true), s.NoiseControl);
        Assert.Equal(NoiseControlMode.Transparency, s.NoiseControl!.Mode);

        var eq = s.Equalizer!;
        Assert.Equal([90, 325, 1500, 6500, 6500], eq.Bands.Select(b => b.FrequencyHz ?? 0));
        Assert.Equal([6.0m, 6.0m, 2.2m, 2.2m, 0.0m], eq.Bands.Select(b => b.GainDb));
        Assert.Equal((-6.0m, 6.0m, true, SoundMode.Equalizer), (eq.MinGainDb, eq.MaxGainDb, eq.BassBoost, eq.SoundMode));

        var mp = s.Multipoint!;
        Assert.Equal(4, mp.Devices.Count);
        Assert.Equal((2, 0), (mp.MaxConnections, mp.OwnIndex));
        Assert.True(mp.Devices[0].IsThisComputer && mp.Devices[0].Connected);
        Assert.All(mp.Devices.Skip(1), d => Assert.False(d.Connected));

        Assert.NotNull(s.LastFullRefresh);
        Assert.Empty(_headset.Unanswered); // nur Commands, die es im Mitschnitt gibt
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Registers_notifications_for_battery_anc_eq_multipoint_and_transparent_hearing()
    {
        // Seit Hardware-Stufe 5 verifiziert, daher auch im Normalbetrieb erlaubt.
        Assert.Equal(ProtocolStatus.Verified, CommandCatalog.RegisterNotification.Status);
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["FF 03 00 01 04 95 00 07 03", "FF 03 00 01 04 95 00 07 0D", "FF 03 00 01 04 95 00 07 08", "FF 03 00 01 04 95 00 07 0A", "FF 03 00 01 04 95 00 07 0C", "FF 03 00 01 04 95 00 07 0B", "FF 03 00 01 04 95 00 07 02"],
            _headset.Requests.Where(r => r.StartsWith("FF 03 00 01 04 95 00 07", StringComparison.Ordinal)));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Notification_updates_state_without_polling()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 1A 83 32")); // Pegel 50
        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 06 83 4B")); // Akku 75

        Assert.Equal(50, service.Store.Current.NoiseControl!.Level);
        Assert.Equal(75, service.Store.Current.BatteryPercent);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Eq_notifications_update_gains_and_bass_boost()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _transport.Receive(Hex.Parse("FF 03 00 05 04 95 10 82 00 14 00 EC 00")); // alle Gains, Format wie am Gerät beobachtet
        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 10 89 00")); // Bass Boost aus

        var eq = service.Store.Current.Equalizer!;
        Assert.Equal([0.0m, 2.0m, 0.0m, -2.0m, 0.0m], eq.Bands.Select(b => b.GainDb));
        Assert.Equal([90, 325, 1500, 6500, 6500], eq.Bands.Select(b => b.FrequencyHz ?? 0)); // bleiben erhalten
        Assert.False(eq.BassBoost);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Undecodable_notification_triggers_refresh_of_its_area()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        var before = _headset.CountRequests("FF 03 00 00 04 95 1A 05");

        _transport.Receive(Hex.Parse("FF 03 00 01 04 95 1A 83 65")); // Pegel 101 – passt nicht zum Format

        await Until(() => _headset.CountRequests("FF 03 00 00 04 95 1A 05") == before + 1, TestContext.Current.CancellationToken);
        Assert.Equal(100, service.Store.Current.NoiseControl!.Level); // kein geratener Wert
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Polling_picks_up_changes_made_elsewhere()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _headset.Set("FF 03 00 00 04 95 06 03", "FF 03 00 01 04 95 07 03 50"); // Akku jetzt 80 %
        _headset.AncEnabled = false; // ANC am Handy ausgeschaltet
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));

        await Until(() => service.Store.Current.BatteryPercent == 80, TestContext.Current.CancellationToken);
        await Until(() => service.Store.Current.NoiseControl!.AncEnabled == false, TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Reconnects_and_reads_again_after_loss()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _transport.SetState(TransportState.Lost);
        await Until(() => service.Store.Current.Connection == ConnectionState.WaitingRetry, TestContext.Current.CancellationToken);
        Assert.Equal(100, service.Store.Current.BatteryPercent); // letzter bestätigter Stand bleibt stehen

        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        Assert.Equal(2, _transport.ConnectCount);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Rejected_read_keeps_last_known_value()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _headset.Set("FF 03 00 00 04 95 06 03", "FF 03 00 01 04 95 07 83 05"); // Error statt Akku
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Until(() => _headset.CountRequests("FF 03 00 00 04 95 06 03") >= 2, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(100, service.Store.Current.BatteryPercent);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Malformed_response_keeps_last_known_value()
    {
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _headset.Set("FF 03 00 00 04 95 06 03", "FF 03 00 01 04 95 07 03 FF"); // 255 % – ungültig (weitere Bytes wären erlaubt, m4.json)
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Until(() => _headset.CountRequests("FF 03 00 00 04 95 06 03") >= 2, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(100, service.Store.Current.BatteryPercent);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Headset_that_stops_answering_leads_to_a_new_connection()
    {
        // Spezifikation §31 „Timeout“: Nach MaxConsecutiveTimeouts Lesefehlern ohne Antwort baut der Service neu auf.
        await using var service = Create(CommandPolicy.Production);
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _headset.Unresponsive = true;
        for (var i = 0; i < 600 && _transport.ConnectCount < 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(_transport.ConnectCount >= 2, "kein neuer Verbindungsaufbau");
        Assert.Equal(100, service.Store.Current.BatteryPercent); // letzter bestätigter Stand bleibt stehen

        _headset.Unresponsive = false;
        for (var i = 0; i < 200 && service.Store.Current.Connection != ConnectionState.Connected; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Restores_transparency_after_a_headset_restart_only_when_enabled(bool enabled, bool expectRestore)
    {
        await using var service = Create(CommandPolicy.Production);
        service.RestoreTransparencyAfterRestart = enabled;
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        Assert.Equal(NoiseControlMode.Transparency, service.Store.Current.NoiseControl!.Mode);

        // Headset aus und wieder an: startet mit ANC, Pegel 100, ohne Transparent Hearing (protocol.md §7.3).
        _headset.TransparentHearing = false;
        _transport.SetState(TransportState.Lost);
        await Until(() => service.Store.Current.Connection == ConnectionState.WaitingRetry, TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        await Until(() => !expectRestore || _headset.TransparentHearing, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(expectRestore ? ["FF 03 00 01 04 95 18 04 01"] : [], _headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
        Assert.Equal(expectRestore ? NoiseControlMode.Transparency : NoiseControlMode.Custom, service.Store.Current.NoiseControl!.Mode);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Does_not_restore_when_the_mode_before_was_not_transparency()
    {
        _headset.TransparentHearing = false; // vorher ANC (Custom)
        await using var service = Create(CommandPolicy.Production);
        service.RestoreTransparencyAfterRestart = true;
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);

        _transport.SetState(TransportState.Lost);
        await Until(() => service.Store.Current.Connection == ConnectionState.WaitingRetry, TestContext.Current.CancellationToken);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Does_not_switch_anything_on_the_first_connection()
    {
        _headset.TransparentHearing = false; // Headset frisch eingeschaltet, App startet danach
        await using var service = Create(CommandPolicy.Production);
        service.RestoreTransparencyAfterRestart = true;
        service.Start();
        await Until(() => service.Store.Current.Connection == ConnectionState.Connected, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_headset.RequestsStartingWith("FF 03 00 01 04 95 18 04"));
    }

    private Momentum4Service Create(CommandPolicy policy) =>
        new(_transport, policy, new Momentum4ServiceOptions(), NullLoggerFactory.Instance, _time);

    private static async Task Until(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition(), "Bedingung nicht erreicht.");
    }
}
