using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetAdditionalGuardTests
{
    [Theory(Timeout = 15000)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task Peer_count_cap_precedes_entry_reads_and_64_entries_are_retained(int count)
    {
        var fixture = new SharedHeadsetFixture();
        while (fixture.Headset.PeerNames.Count < count)
        {
            fixture.Headset.PeerNames.Add($"Peer {fixture.Headset.PeerNames.Count}");
            fixture.Headset.PeerConnected.Add(false);
        }
        await using var service = await fixture.StartAsync();
        if (count == 64)
        {
            var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Equal(64, snapshot.Peers.Count);
            Assert.True(snapshot.PeersAvailable);
        }
        else
        {
            await Assert.ThrowsAsync<ProtocolFormatException>(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken));
            Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value == 0x1401);
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x3603)]
    [InlineData(0x3605)]
    [InlineData(0x0804)]
    public async Task Spatial_requires_fresh_complete_capabilities(int getter)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.ExtraValues[0x3605] = 1;
        await using var service = await fixture.StartAsync();
        Assert.Contains(HeadsetControl.HeadTracking, (await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken)).Writable);
        fixture.Rejected.Add((ushort)getter);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.HeadTracking, true, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Stored_tracking_can_be_disabled_while_immersive_usage_is_off()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.ExtraValues[0x3603] = 1;
        fixture.ExtraValues[0x3605] = 0;
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(before.Settings.HeadTracking);
        Assert.False(before.Settings.ImmersiveAudio);
        var after = await service.SetBooleanControlAsync(HeadsetBooleanControl.HeadTracking, false, TestContext.Current.CancellationToken);
        Assert.False(after.Settings.HeadTracking);
        Assert.False(after.Settings.ImmersiveAudio);
        Assert.Equal(0x3602, Assert.Single(fixture.Writes).Command.Value);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ack")]
    [InlineData("mismatch")]
    [InlineData("sibling")]
    [InlineData("mode")]
    [InlineData("identity")]
    [InlineData("rejected")]
    public async Task Spatial_post_write_checks_ack_target_sibling_mode_identity_and_capability(string failure)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.ExtraValues[0x3605] = 1;
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value != 0x3604) return;
            switch (failure)
            {
                case "ack": fixture.Overrides[0x3604] = [0]; break;
                case "mismatch": fixture.Overrides[0x3605] = [1]; break;
                case "sibling": fixture.ExtraValues[0x3603] = 1; break;
                case "mode": fixture.Headset.SoundMode = 2; break;
                case "identity": fixture.Firmware = [0, 6, 0, 21, 0, 6]; break;
                case "rejected": fixture.Rejected.Add(0x3603); break;
            }
        };
        await Assert.ThrowsAnyAsync<Exception>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.ImmersiveAudio, false, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x3604);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Theory(Timeout = 15000)]
    [InlineData(75, 75, false, false)]
    [InlineData(100, 100, true, false)]
    [InlineData(75, 50, false, true)]
    [InlineData(100, 100, false, true)]
    public async Task Transparency_noop_requires_stored_register_active_level_and_status(int stored, int active, bool enabled, bool writes)
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Overrides[0x1803] = [(byte)stored];
        fixture.Headset.AncLevel = (byte)active;
        fixture.Headset.TransparentHearing = enabled;
        await using var service = await fixture.StartAsync();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1804) fixture.Overrides.Remove(0x1803);
        };
        var after = await service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, stored, TestContext.Current.CancellationToken);
        Assert.Equal(stored, after.Settings.AncStrength);
        Assert.Equal(stored, after.Settings.TransparencyLevel);
        Assert.Equal(stored == 100, after.Settings.Transparency);
        Assert.Equal(writes, fixture.Writes.Count > 0);
    }

    [Fact(Timeout = 15000)]
    public async Task Full_refresh_observes_all_groups_and_notes_remain_unique_and_bounded()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Rejected.UnionWith(new ushort[] { 0x0806, 0x060a, 0x060b, 0x2802, 0x1400, 0x3605 });
        await using var service = await fixture.StartAsync();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            fixture.Requests.Clear();
            var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.Contains(fixture.Requests, frame => frame.Command.Value == 0x0603);
            Assert.Contains(fixture.Requests, frame => frame.Command.Value == 0x1a05);
            Assert.Contains(fixture.Requests, frame => frame.Command.Value == 0x1607);
            Assert.Contains(fixture.Requests, frame => frame.Command.Value == 0x1000);
            Assert.Equal(snapshot.Notes.Distinct().Count(), snapshot.Notes.Count);
            Assert.InRange(snapshot.Notes.Count, 1, 32);
        }
    }
    [Fact(Timeout = 15000)]
    public async Task Legacy_rollback_blind_ack_reports_remaining_partial_curve()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Headset.RejectEqBand = 2;
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1001 && frame.Payload[0] == 2)
            {
                fixture.Headset.IgnoreWrites = true;
            }
        };
        await Assert.ThrowsAsync<HeadsetPartialWriteException>(() => service.SetEqGainsAsync(Enumerable.Repeat(1m, 8).ToArray(), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 10, 10, 0, 0, 0, 0, 0, 0 }, fixture.Headset.EqGains);
        Assert.Equal(new byte[] { 0, 1, 2, 1, 0 }, fixture.Writes.Select(frame => frame.Payload[0]));
    }

}
