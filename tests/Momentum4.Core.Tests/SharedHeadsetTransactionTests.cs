using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetTransactionTests
{
    [Theory(Timeout = 15000)]
    [InlineData("own")]
    [InlineData("index")]
    [InlineData("name")]
    [InlineData("priority")]
    [InlineData("ambiguous")]
    [InlineData("unknown")]
    [InlineData("own_off")]
    [InlineData("full")]
    [InlineData("missing_max")]
    [InlineData("missing_own")]
    [InlineData("missing_entry")]
    public async Task Peer_selection_identity_capacity_and_presence_guards_precede_write(string failure)
    {
        var fixture = M4();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var target = before.Peers[1];
        var selected = new HeadsetPeerSelection(target.Index, target.Name, target.Priority, target.IsThisComputer);
        switch (failure)
        {
            case "own": selected = new HeadsetPeerSelection(0, before.Peers[0].Name, before.Peers[0].Priority, true); break;
            case "index": selected = selected with { Index = 7 }; break;
            case "name": selected = selected with { Name = "Other" }; break;
            case "priority": selected = selected with { Priority = 7 }; break;
            case "ambiguous": fixture.Headset.PeerNames[2] = target.Name; break;
            case "unknown": fixture.Overrides[0x1401] = [0, 0, 2, 80, 67]; break;
            case "own_off": fixture.Headset.PeerConnected[0] = false; break;
            case "full": fixture.Headset.PeerConnected[2] = true; break;
            case "missing_max": fixture.Rejected.Add(0x1409); break;
            case "missing_own": fixture.Rejected.Add(0x1407); break;
            case "missing_entry": fixture.Rejected.Add(0x1401); break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => service.ConnectControlPeerAsync(selected, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Peer_connect_is_confirmed_and_noop_when_already_connected()
    {
        var fixture = M4();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var peer = before.Peers[1];
        var selected = new HeadsetPeerSelection(peer.Index, peer.Name, peer.Priority, peer.IsThisComputer);
        var after = await service.ConnectControlPeerAsync(selected, TestContext.Current.CancellationToken);
        Assert.True(after.Peers[1].Connected);
        Assert.True(after.Peers[0].Connected);
        await service.ConnectControlPeerAsync(selected, TestContext.Current.CancellationToken);
        Assert.Equal(0x1402, Assert.Single(fixture.Writes).Command.Value);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ack")]
    [InlineData("echo")]
    [InlineData("state")]
    [InlineData("length")]
    [InlineData("target")]
    [InlineData("own")]
    [InlineData("own_name")]
    [InlineData("model")]
    [InlineData("capability")]
    public async Task Peer_ack_poll_and_final_identity_failures_never_replay_write(string failure)
    {
        var fixture = M4();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var peer = before.Peers[1];
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1402 && failure == "ack") fixture.Overrides[0x1402] = [0];
            if (frame.Command.Value != 0x1404) return;
            switch (failure)
            {
                case "echo": fixture.Overrides[0x1404] = [0, 1]; break;
                case "state": fixture.Overrides[0x1404] = [1, 2]; break;
                case "length": fixture.Overrides[0x1404] = [1, 1, 0]; break;
                case "target": fixture.Headset.PeerConnected[1] = false; fixture.Overrides[0x1404] = [1, 1]; break;
                case "own": fixture.Headset.PeerConnected[0] = false; break;
                case "own_name": fixture.Headset.PeerNames[0] = "Other PC"; break;
                case "model": fixture.Model = "M4AEBT White"; break;
                case "capability": fixture.Rejected.Add(0x1400); break;
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.ConnectControlPeerAsync(new HeadsetPeerSelection(peer.Index, peer.Name, peer.Priority, peer.IsThisComputer), TestContext.Current.CancellationToken));
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x1402);
    }

    [Fact(Timeout = 15000)]
    public async Task Peer_poll_is_bounded_to_eight_seconds_and_never_resends()
    {
        var time = new FakeTimeProvider();
        var fixture = M4();
        fixture.Time = time;
        fixture.PeerConnectTimeout = TimeSpan.FromSeconds(20);
        fixture.Headset.PeersNeverConnect = true;
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var peer = before.Peers[1];
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1404) time.Advance(Momentum4Service.ControlPeerPollInterval);
        };
        var started = time.GetTimestamp();
        var operation = service.ConnectControlPeerAsync(new HeadsetPeerSelection(peer.Index, peer.Name, peer.Priority, false), TestContext.Current.CancellationToken);
        for (var iteration = 0; iteration < 100 && !operation.IsCompleted; iteration++)
        {
            time.Advance(Momentum4Service.ControlPeerPollInterval);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<SettingNotAppliedException<string>>(() => operation);
        Assert.InRange(time.GetElapsedTime(started), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8.6));
        Assert.Equal(TimeSpan.FromSeconds(8), Momentum4Service.ControlPeerPollTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(300), Momentum4Service.ControlPeerPollInterval);
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x1402);
        Assert.True(fixture.Requests.Count(frame => frame.Command.Value == 0x1404) > 1);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ack")]
    [InlineData("level")]
    [InlineData("anc_level")]
    [InlineData("flag")]
    [InlineData("caps")]
    [InlineData("identity")]
    public async Task Failed_transparency_preparation_never_sends_level_setter(string failure)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != 0x1804) return;
            switch (failure)
            {
                case "ack": fixture.Overrides[0x1804] = [1]; break;
                case "level": fixture.Overrides[0x1803] = [75]; break;
                case "anc_level": fixture.Overrides[0x1a03] = [75]; break;
                case "flag": fixture.Overrides[0x1805] = [0]; break;
                case "caps": fixture.Rejected.Add(0x1805); break;
                case "identity": fixture.Model = "M5AEBT White"; break;
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, 75, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value == 0x1802);
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x1804);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x1803, "32")]
    [InlineData(0x1a03, "32")]
    [InlineData(0x1805, "01")]
    [InlineData(0x1a05, "00")]
    [InlineData(0x1a01, "010002000301")]
    [InlineData(0x1a01, "010102000300")]
    public async Task Final_transparency_readback_checks_both_registers_flag_and_each_sibling(int getter, string payload)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AncTable = [1, 0, 2, 0, 3, 0];
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1802) fixture.Overrides[(ushort)getter] = Hex.Parse(payload);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, 75, TestContext.Current.CancellationToken));
        Assert.Equal(new ushort[] { 0x1804, 0x1802 }, fixture.Requests.Where(frame => frame.Command.Value is 0x1804 or 0x1802).Select(frame => frame.Command.Value));
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x0806, "01")]
    [InlineData(0x1801, "01")]
    [InlineData(0x0601, "000708")]
    public async Task Battery_protection_write_preserves_each_ancillary_sibling(int getter, string payload)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x060a) fixture.Overrides[(ushort)getter] = Hex.Parse(payload);
        };
        await Assert.ThrowsAnyAsync<SettingNotAppliedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.BatteryProtection, false, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x060a);
    }

    [Theory(Timeout = 15000)]
    [InlineData(901)]
    [InlineData(7200)]
    [InlineData(65535)]
    public async Task Custom_timer_reads_do_not_expand_verified_write_choices(int seconds)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AutoPowerOffSeconds = seconds;
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(seconds, before.Settings.AutoPowerOff);
        Assert.Equal(new[] { 0, 900, 1800, 3600 }, before.Choices[HeadsetIntegerControl.AutoPowerOff]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.AutoPowerOff, seconds, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        Assert.Equal(1800, (await service.SetIntegerControlAsync(HeadsetIntegerControl.AutoPowerOff, 1800, TestContext.Current.CancellationToken)).Settings.AutoPowerOff);
    }

    [Fact(Timeout = 15000)]
    public async Task Names_read_up_to_128_bytes_and_m4_writes_30_utf8_bytes()
    {
        var fixture = M4();
        fixture.Headset.DeviceName = new string('ü', 60);
        await using var service = await fixture.StartAsync();
        Assert.Equal(fixture.Headset.DeviceName, (await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).DeviceName);
        Assert.Equal(new string('x', 30), (await service.RenameControlAsync(new string('x', 30), TestContext.Current.CancellationToken)).DeviceName);
        Assert.Single(fixture.Writes);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameControlAsync(new string('ü', 16), TestContext.Current.CancellationToken));
        Assert.Single(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Eq_post_write_identity_or_ordered_frequency_change_reports_partial_and_invalidates()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1001) fixture.Model = "M5AEBT White";
        };
        await Assert.ThrowsAsync<HeadsetPartialWriteException>(() => service.SetControlEqBandAsync(0, 1m, new HeadsetEqTopology(before.Model, before.EqFrequenciesHz), TestContext.Current.CancellationToken));
        Assert.Equal(0x1001, Assert.Single(fixture.Writes).Command.Value);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x2802, HeadsetControl.Rename)]
    [InlineData(0x1400, HeadsetControl.ConnectPeer)]
    [InlineData(0x1407, HeadsetControl.ConnectPeer)]
    [InlineData(0x1409, HeadsetControl.ConnectPeer)]
    [InlineData(0x1401, HeadsetControl.ConnectPeer)]
    public async Task Optional_metadata_rejection_revokes_only_affected_capability(int getter, HeadsetControl control)
    {
        var fixture = M4();
        await using var service = await fixture.StartAsync();
        Assert.Contains(control, (await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).Writable);
        fixture.Rejected.Add((ushort)getter);
        var after = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(control, after.Writable);
        Assert.Contains(HeadsetControl.TouchControls, after.Writable);
        Assert.True(after.VerifiedSession);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ack")]
    [InlineData("mismatch")]
    [InlineData("model")]
    [InlineData("reject")]
    public async Task Rename_rejects_bad_ack_mismatch_identity_and_revoked_readback(string failure)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != 0x2801) return;
            switch (failure)
            {
                case "ack": fixture.Overrides[0x2801] = [0]; break;
                case "mismatch": fixture.Overrides[0x2802] = System.Text.Encoding.UTF8.GetBytes("Other"); break;
                case "model": fixture.Model = "M5AEBT White"; break;
                case "reject": fixture.Rejected.Add(0x2802); break;
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.RenameControlAsync("Changed", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x2801);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Theory(Timeout = 15000)]
    [InlineData("frequency")]
    [InlineData("count")]
    [InlineData("range")]
    [InlineData("missing")]
    public async Task Fresh_eq_count_frequency_and_gain_range_override_cache_before_write(string change)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        switch (change)
        {
            case "frequency": fixture.Frequencies[0] = 61; break;
            case "count": fixture.Headset.EqGains = [0]; fixture.Overrides[0x1000] = [1, 196, 60]; break;
            case "range": fixture.Overrides[0x1000] = [8, 246, 10]; break;
            case "missing": fixture.Rejected.Add(0x100b); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetControlEqBandAsync(0, 2m, new HeadsetEqTopology(before.Model, before.EqFrequenciesHz), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Eq_final_frequency_remapping_fails_even_when_gain_matches()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1001) fixture.Frequencies[0] = 61;
        };
        await Assert.ThrowsAsync<HeadsetPartialWriteException>(() => service.SetControlEqBandAsync(0, 1m, new HeadsetEqTopology(before.Model, before.EqFrequenciesHz), TestContext.Current.CancellationToken));
        Assert.Equal(10, fixture.Headset.EqGains[0]);
        Assert.Single(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Cached_noise_noop_rechecks_external_state_and_preserves_custom_strength()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.TransparentHearing = false;
        fixture.Headset.AncLevel = 30;
        fixture.Headset.AncTable = [1, 0, 2, 0, 3, 1];
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Headset.AncTable = [1, 1, 2, 0, 3, 1];
        var after = await service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, after.Settings.AntiWind);
        Assert.Equal(30, after.Settings.AncStrength);
        Assert.True(after.Settings.AdaptiveAnc);
        Assert.Equal(0x1a00, Assert.Single(fixture.Writes).Command.Value);
    }

    private static SharedHeadsetFixture M4() => new() { Model = "M4AEBT", Firmware = [0, 3, 0, 37, 0, 3] };
}
