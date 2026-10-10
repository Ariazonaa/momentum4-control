using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Features;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetCompatibilityEdgeTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sound_mode_accepts_application_at_1900_milliseconds_with_one_setter(bool windows)
    {
        var time = new FakeTimeProvider();
        var fixture = new SharedHeadsetFixture { Model = "M4AEBT", Firmware = [0, 3, 0, 37, 0, 3], Time = time };
        fixture.Headset.SoundMode = 1;
        await using var service = await fixture.StartAsync();
        var writeSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long? writtenAt = null;
        var readAt = new List<TimeSpan>();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x0803)
            {
                writtenAt = time.GetTimestamp();
                writeSeen.SetResult();
            }

            if (frame.Command.Value == 0x0804 && writtenAt is { } written)
            {
                var elapsed = time.GetElapsedTime(written);
                readAt.Add(elapsed);
                if (elapsed < TimeSpan.FromMilliseconds(1900)) fixture.Overrides[0x0804] = [0, 1];
                else fixture.Overrides.Remove(0x0804);
            }
        };
        var operation = SetSoundModeAsync(service, windows);
        await writeSeen.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(operation.IsCompleted);
        for (var iteration = 0; iteration < 80 && !operation.IsCompleted; iteration++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            if (!operation.IsCompleted) time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(operation.IsCompleted);
        Assert.Equal(2, await operation);
        Assert.Equal(TimeSpan.Zero, readAt[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), readAt[^1]);
        Assert.Contains(readAt, elapsed => elapsed == TimeSpan.FromMilliseconds(1750));
        Assert.Equal(0x0803, Assert.Single(fixture.Writes).Command.Value);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.True(service.Features.Writable);
    }

    [Fact(Timeout = 15000)]
    public async Task M5_observed_automatic_antiwind_is_retained_but_cannot_be_written()
    {
        var fixture = new SharedHeadsetFixture();
        fixture.Headset.AncTable = [1, 2, 2, 0, 3, 0];
        fixture.Headset.TransparentHearing = false;
        await using var service = await fixture.StartAsync();
        var snapshot = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Settings.AntiWind);
        Assert.Equal(new[] { 0, 1 }, snapshot.Choices[HeadsetIntegerControl.AntiWind]);
        Assert.Contains(HeadsetControl.AntiWind, snapshot.Writable);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetIntegerControlAsync(
            HeadsetIntegerControl.AntiWind, 2, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetAntiWindAsync(
            AntiWindMode.Automatic, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.True(service.Features.Writable);
        var refreshed = await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(refreshed.VerifiedSession);
        Assert.Equal(2, refreshed.Settings.AntiWind);
        var changed = await service.SetIntegerControlAsync(HeadsetIntegerControl.AntiWind, 0, TestContext.Current.CancellationToken);
        Assert.Equal(0, changed.Settings.AntiWind);
        Assert.Equal(new byte[] { 1, 0 }, Assert.Single(fixture.Writes).Payload);
    }

    [Theory(Timeout = 15000)]
    [InlineData(3)]
    [InlineData(255)]
    public async Task M5_unknown_antiwind_observation_revokes_session(byte observed)
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        fixture.Headset.AncTable = [1, observed, 2, 0, 3, 0];
        await Assert.ThrowsAsync<ProtocolFormatException>(() => service.GetControlSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Writes);
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.False(service.Features.Writable);
    }

    private static async Task<int?> SetSoundModeAsync(Momentum4Service service, bool windows)
    {
        if (windows) return (int?)(await service.SetSoundModeAsync(SoundMode.Podcast, TestContext.Current.CancellationToken)).SoundMode;
        return (await service.SetIntegerControlAsync(HeadsetIntegerControl.SoundMode, 2, TestContext.Current.CancellationToken)).Settings.SoundMode;
    }
}
