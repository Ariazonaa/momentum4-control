using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Device;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetParityTests
{
    [Fact(Timeout = 15000)]
    public async Task Targeted_behavior_refresh_reads_identity_and_preserves_unrelated_snapshot()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var before = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Requests.Clear();
        fixture.Headset.AutoAnswer = true;
        var after = await service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken);
        Assert.Equal(new ushort[] { 0x1206, 0x1201 }, fixture.Requests.Take(2).Select(frame => frame.Command.Value));
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value is 0x0603 or 0x1000 or 0x2802);
        Assert.Equal(before.Battery, after.Battery);
        Assert.Equal(before.DeviceName, after.DeviceName);
        Assert.False(before.Settings.AutoAnswer);
        Assert.True(after.Settings.AutoAnswer);
        Assert.True(after.Settings.SmartPause);
    }

    [Fact(Timeout = 15000)]
    public async Task Snapshots_are_immutable_and_caller_eq_collections_are_frozen_before_waiting()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(() => ((IList<decimal>)snapshot.EqGainsDb)[0] = 6);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)snapshot.EqFrequenciesHz)[0] = 1);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)snapshot.Choices[HeadsetIntegerControl.AntiWind])[0] = 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1206 && first)
            {
                first = false;
                started.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        var blocker = Task.Run(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var gains = Enumerable.Repeat(1m, 8).ToArray();
        var frequencies = snapshot.EqFrequenciesHz.ToArray();
        var operation = service.SetControlEqualizerAsync(gains, new HeadsetEqTopology(snapshot.Model, frequencies), TestContext.Current.CancellationToken);
        gains[0] = 5;
        frequencies[0] = 1;
        release.SetResult();
        await blocker;
        var after = await operation;
        Assert.Equal(Enumerable.Repeat(1m, 8), after.EqGainsDb);
        Assert.Equal(Enumerable.Repeat(0m, 8), snapshot.EqGainsDb);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x1a05)]
    [InlineData(0x1a03)]
    [InlineData(0x1805)]
    [InlineData(0x1a01)]
    [InlineData(0x1803)]
    public async Task Rejected_noise_prerequisite_denies_before_any_transparency_write(int getter)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Rejected.Add((ushort)getter);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, 75, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x1a05)]
    [InlineData(0x1a03)]
    [InlineData(0x1805)]
    [InlineData(0x1a01)]
    public async Task Rejected_post_noise_prerequisite_never_sends_correction(int getter)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x1a00)
            {
                fixture.Rejected.Add((ushort)getter);
            }
        };
        await Assert.ThrowsAsync<ProtocolFormatException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken));
        Assert.Equal(0x1a00, Assert.Single(fixture.Writes).Command.Value);
    }

    [Theory(Timeout = 15000)]
    [InlineData(0x1607, "")]
    [InlineData(0x1607, "02")]
    [InlineData(0x1803, "65")]
    [InlineData(0x3603, "0100")]
    [InlineData(0x3605, "02")]
    [InlineData(0x060b, "02")]
    [InlineData(0x0602, "03")]
    [InlineData(0x0602, "000000")]
    [InlineData(0x0402, "04")]
    [InlineData(0x2001, "04")]
    [InlineData(0x1400, "0041")]
    [InlineData(0x1407, "04")]
    [InlineData(0x1409, "00")]
    [InlineData(0x1409, "41")]
    [InlineData(0x1000, "11c43c")]
    [InlineData(0x1000, "080a00")]
    [InlineData(0x1003, "7f00000000000000")]
    [InlineData(0x1a01, "01010100")]
    [InlineData(0x1a01, "010302000300")]
    [InlineData(0x2802, "c3")]
    [InlineData(0x2802, "")]
    public async Task Malformed_optional_read_is_fatal_and_precedes_any_write(int getter, string payload)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        fixture.Overrides[(ushort)getter] = Hex.Parse(payload);
        await Assert.ThrowsAsync<ProtocolFormatException>(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Writes);
    }

    [Theory(Timeout = 15000)]
    [InlineData(HeadsetIntegerControl.AncStrength, -1)]
    [InlineData(HeadsetIntegerControl.AncStrength, 101)]
    [InlineData(HeadsetIntegerControl.TransparencyLevel, -1)]
    [InlineData(HeadsetIntegerControl.TransparencyLevel, 101)]
    [InlineData(HeadsetIntegerControl.Sidetone, -1)]
    [InlineData(HeadsetIntegerControl.Sidetone, 6)]
    [InlineData(HeadsetIntegerControl.AutoPowerOff, 600)]
    [InlineData(HeadsetIntegerControl.AudioPrompts, 3)]
    [InlineData(HeadsetIntegerControl.SoundMode, 3)]
    public async Task Typed_integer_domains_reject_before_write(HeadsetIntegerControl control, int value)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIntegerControlAsync(control, value, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
    }

    [Theory(Timeout = 15000)]
    [InlineData("M4AEBT", 3, 37, 3)]
    [InlineData("M5AEBT", 6, 21, 6)]
    [InlineData("Unknown", 6, 21, 5)]
    public async Task Unverified_new_profiles_never_issue_spatial_battery_or_transparency_commands(string model, int major, int minor, int patch)
    {
        var fixture = new SharedHeadsetFixture { Model = model, Firmware = [0, (byte)major, 0, (byte)minor, 0, (byte)patch] };
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Null(snapshot.Settings.ImmersiveAudio);
        Assert.Null(snapshot.Settings.HeadTracking);
        Assert.Null(snapshot.Settings.BatteryProtection);
        Assert.Null(snapshot.Settings.TransparencyLevel);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.ImmersiveAudio, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.BatteryProtection, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.TransparencyLevel, 75, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value is 0x3602 or 0x3603 or 0x3604 or 0x3605 or 0x060a or 0x060b or 0x1802 or 0x1803);
    }

    [Fact(Timeout = 15000)]
    public async Task M4_sidetone_read_succeeds_but_unverified_setter_is_denied()
    {
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 37, 0, 3] };
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, snapshot.Settings.Sidetone);
        Assert.DoesNotContain(HeadsetControl.Sidetone, snapshot.Writable);
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetIntegerControlAsync(HeadsetIntegerControl.Sidetone, 1, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value == 0x0805);
    }

    [Fact(Timeout = 15000)]
    public async Task Equivalent_windows_and_shared_boolean_actions_reach_same_guarded_setter_and_readback()
    {
        var windowsFixture = new SharedHeadsetFixture();
        var sharedFixture = new SharedHeadsetFixture();
        await using var windows = await windowsFixture.StartAsync();
        await using var shared = await sharedFixture.StartAsync();
        Assert.True((await windows.SetBehaviorAsync(BehaviorSetting.SmartPause, true, TestContext.Current.CancellationToken)).SmartPause);
        Assert.True((await shared.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken)).Settings.SmartPause);
        Assert.Equal(Assert.Single(windowsFixture.Writes).Raw, Assert.Single(sharedFixture.Writes).Raw);
        Assert.Contains(windowsFixture.Requests, frame => frame.Command.Value == 0x1206);
        Assert.Contains(sharedFixture.Requests, frame => frame.Command.Value == 0x1206);
    }

    [Fact(Timeout = 15000)]
    public async Task Absolute_operation_budget_is_not_renewed_by_progressing_replies()
    {
        var time = new FakeTimeProvider();
        var fixture = new SharedHeadsetFixture { Time = time };
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        var start = time.GetTimestamp();
        fixture.OnRequest = _ => time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken));
        Assert.Equal(Momentum4Service.ControlOperationTimeout, time.GetElapsedTime(start));
        Assert.Single(fixture.Writes);
    }

    [Fact(Timeout = 15000)]
    public async Task Forced_windows_polling_cannot_interleave_shared_transaction()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync(polling: true);
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.Requests.Clear();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x080d && first)
            {
                first = false;
                blocked.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        var operation = Task.Run(() => service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        service.RequestRefresh(RefreshScope.Battery);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Requests, frame => frame.Command.Value == 0x0603);
        release.SetResult();
        await operation;
        for (var attempt = 0; attempt < 200 && !fixture.Requests.Any(frame => frame.Command.Value == 0x0603); attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        var batteryIndex = fixture.Requests.FindIndex(frame => frame.Command.Value == 0x0603);
        var lastBehaviorRead = fixture.Requests.FindLastIndex(frame => frame.Command.Value == 0x0802);
        Assert.True(batteryIndex > lastBehaviorRead && lastBehaviorRead > fixture.Requests.FindIndex(frame => frame.Command.Value == 0x080c));
    }
}
