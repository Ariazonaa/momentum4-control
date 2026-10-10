using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

public sealed class DongleServiceTests
{
    [Fact]
    public async Task StatusDecodesCapturedRepliesAndIgnoresEvents()
    {
        var peer = new DonglePeer { Events = true, Supported = 0x800f };
        var status = await new DongleService(peer, peer.Clock).StatusAsync(peer.Target, TestContext.Current.CancellationToken);
        Assert.Equal(new FirmwareVersion(3, 11, 0), status.Firmware);
        Assert.Equal((ushort)0x800f, status.SupportedCodecMask);
        Assert.Equal((ushort)8, status.CodecMask);
        Assert.Equal(16, status.SampleBits); Assert.Equal(44100, status.SampleRate);
        Assert.True(peer.LeaseDisposed); Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed));
    }

    [Fact]
    public async Task CodecWritesExactSetterOnceAndWaitsForStableNegotiation()
    {
        var peer = new DonglePeer();
        peer.OnControl = command => { if (command == 0x12 && peer.Setters > 0) peer.Active = peer.Elapsed >= 0.3 ? (ushort)4 : (ushort)8; };
        var task = new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken);
        var status = await peer.Complete(task);
        Assert.Equal((ushort)4, status.CodecMask);
        Assert.Equal(1, peer.Setters); Assert.True(peer.Elapsed >= 0.8);
        Assert.Contains(peer.Writes, frame => frame.SequenceEqual(Convert.FromHexString("34fe04020400").Concat(new byte[58])));
        Assert.True(peer.LeaseDisposed);
    }

    [Fact]
    public async Task CodecNoopAndConnectedIdleAreSafe()
    {
        var peer = new DonglePeer();
        await new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxLossless, TestContext.Current.CancellationToken);
        Assert.Equal(0, peer.Setters); Assert.Equal(0, peer.Elapsed);
        peer = new DonglePeer { State = 2 };
        peer.OnControl = _ => { if (peer.Setters > 0) peer.Active = 4; };
        var status = await peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken));
        Assert.Equal(2, status.State);
    }

    [Fact]
    public async Task InvalidCodecAndUnsafeStateDenyBeforeSetter()
    {
        foreach (var codec in new[] { 0, 3, 64, -1 })
        {
            var peer = new DonglePeer();
            await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, (DongleCodec)codec, TestContext.Current.CancellationToken)));
            Assert.Equal(0, peer.Acquisitions);
        }
        foreach (var (support, state, mode) in new[] { (8, 3, 0), (0, 3, 0), (15, 0, 0), (15, 1, 0), (15, 4, 0), (15, 255, 0), (15, 3, 1), (15, 3, 255) })
        {
            var peer = new DonglePeer { Supported = (ushort)support, State = (byte)state, Mode = (byte)mode };
            await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
            Assert.Equal(0, peer.Setters); Assert.True(peer.LeaseDisposed);
        }
    }

    [Fact]
    public async Task MalformedStatusAndAcknowledgementNeverClaimSuccess()
    {
        foreach (var command in new byte[] { 3, 5, 6, 1, 4 })
        {
            var peer = new DonglePeer(); peer.ControlOverrides[command] = DonglePeer.ControlReply(command, []);
            await Assert.ThrowsAnyAsync<Exception>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
            Assert.Equal(command == 4 ? 1 : 0, peer.Setters); Assert.True(peer.LeaseDisposed);
        }
        var rejected = new DonglePeer(); rejected.OnControl = _ => { if (rejected.Setters > 0) rejected.Active = 4; }; rejected.ControlOverrides[4] = DonglePeer.ControlReply(4, [1]);
        await Assert.ThrowsAsync<DongleException>(() => rejected.Complete(new DongleService(rejected, rejected.Clock).SetCodecAsync(rejected.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task AckWithoutStableActiveCodecTimesOutWithoutRetry()
    {
        foreach (var (active, state, mode) in new[] { (8, 3, 0), (12, 3, 0), (4, 1, 0), (4, 3, 1) })
        {
            var peer = new DonglePeer(); peer.OnControl = _ => { if (peer.Setters > 0) { peer.Active = (ushort)active; peer.State = (byte)state; peer.Mode = (byte)mode; } };
            var error = await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
            Assert.Contains("unconfirmed", error.Message); Assert.Equal(1, peer.Setters); Assert.True(peer.Elapsed <= 15.1);
            Assert.True(peer.LeaseDisposed);
        }
    }

    [Fact]
    public async Task TransientMatchResetsStabilityWindow()
    {
        var peer = new DonglePeer();
        peer.OnControl = _ => { if (peer.Setters > 0) peer.Active = peer.Elapsed < 0.3 || peer.Elapsed >= 0.7 ? (ushort)4 : (ushort)8; };
        await peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken));
        Assert.True(peer.Elapsed >= 1.2); Assert.Equal(1, peer.Setters);
    }

    [Fact]
    public async Task ChangedInstanceReopensReadOnlyAndRestartsWindow()
    {
        var peer = new DonglePeer(); var changed = false;
        peer.OnControl = _ =>
        {
            if (peer.Setters == 0) return;
            peer.Active = 4;
            if (!changed && peer.Elapsed >= 0.3) { changed = true; peer.Observation = peer.Observation with { ChangeStamp = 301 }; }
        };
        await peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken));
        Assert.True(changed); Assert.Equal(2, peer.Endpoints.Count); Assert.Equal(1, peer.Setters); Assert.True(peer.Elapsed >= 0.8);
    }

    [Fact]
    public async Task ReplacementDuringFinalSnapshotRejectsStaleSuccess()
    {
        var peer = new DonglePeer();
        peer.OnControl = command =>
        {
            if (peer.Setters > 0) peer.Active = 4;
            if (command == 0x15 && peer.Elapsed >= 0.5) peer.Observation = peer.Observation with { Target = peer.Target with { Identity = "foreign" } };
        };
        var error = await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
        Assert.Contains("identity", error.Message); Assert.Equal(1, peer.Setters);
    }

    [Fact]
    public async Task CancellationAndPartialWriteReleaseResources()
    {
        var peer = new DonglePeer { PartialWrite = true };
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).StatusAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(peer.LeaseDisposed); Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed));
        peer = new DonglePeer(); using var cancellation = new CancellationTokenSource();
        peer.OnControl = command => { if (command == 4) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, cancellation.Token)));
        Assert.True(peer.LeaseDisposed); Assert.Equal(1, peer.Setters);
    }

    [Fact]
    public async Task CancellationAtEveryCodecStageClosesTheOwnedLease()
    {
        foreach (var phase in new[] { "start", "prewrite", "ack", "confirm", "removed" })
        {
            var peer = new DonglePeer(); using var cancellation = new CancellationTokenSource();
            if (phase == "start") cancellation.Cancel();
            peer.OnControl = command =>
            {
                if (phase == "prewrite" && command == 0x15 && peer.Setters == 0 || phase == "ack" && command == 4 || phase == "confirm" && command == 0x12 && peer.Setters > 0) cancellation.Cancel();
            };
            if (phase == "removed") peer.OnObserve = () =>
            {
                if (peer.Setters == 0) return new[] { peer.Observation };
                if (peer.Elapsed >= 0.3) cancellation.Cancel();
                return Array.Empty<DongleObservation>();
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, cancellation.Token)));
            Assert.True(peer.Acquisitions == 0 || peer.LeaseDisposed); Assert.Equal(phase is "start" or "prewrite" ? 0 : 1, peer.Setters);
            Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed));
        }
    }

    [Fact]
    public async Task EventFloodAndWaitingReadHaveAbsoluteCommandDeadlines()
    {
        var peer = new DonglePeer { EventFlood = true };
        await Assert.ThrowsAnyAsync<Exception>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
        Assert.True(peer.Elapsed >= 3 && peer.Elapsed <= 3.1); Assert.Equal(0, peer.Setters); Assert.True(peer.LeaseDisposed);
        peer = new DonglePeer { StallRead = true };
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).StatusAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(peer.Elapsed >= 3 && peer.Elapsed <= 3.1); Assert.True(peer.LeaseDisposed); Assert.All(peer.ReadTimeouts, timeout => Assert.True(timeout <= TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SameIdentityReenumerationAndInspectionRemovalOnlyRetryReads()
    {
        var peer = new DonglePeer { RemoveOnSetter = true }; var replacement = peer.Target with { ControlPath = "new-control", UpgradePath = "new-upgrade" };
        peer.OnControl = command =>
        {
            Assert.False(peer.LeaseDisposed);
            if (command == 4) peer.Observation = peer.Observation with { Target = replacement, DeviceNumber = 5 };
            if (peer.Setters > 0) peer.Active = 4;
        };
        var status = await peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken));
        Assert.Equal(replacement, status.Target); Assert.Equal(2, peer.Endpoints.Count); Assert.Equal(1, peer.Setters);
        var inspecting = new DonglePeer(); var removed = false;
        inspecting.OnControl = _ => { if (inspecting.Setters > 0) inspecting.Active = 4; };
        inspecting.OnObserve = () =>
        {
            if (inspecting.Setters > 0 && !removed) { removed = true; throw new DongleDisconnectedException("removed while inspecting"); }
            return new[] { inspecting.Observation };
        };
        await inspecting.Complete(new DongleService(inspecting, inspecting.Clock).SetCodecAsync(inspecting.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken));
        Assert.True(removed); Assert.Equal(1, inspecting.Setters); Assert.Equal(2, inspecting.Endpoints.Count);
    }

    [Fact]
    public async Task PermanentRemovalAndForeignOrAmbiguousReplacementNeverRetrySetter()
    {
        foreach (var replacement in new[] { "absent", "foreign", "ambiguous" })
        {
            var peer = new DonglePeer(); peer.OnControl = _ => { if (peer.Setters > 0) peer.Active = 4; };
            peer.OnObserve = () => peer.Setters == 0 ? new[] { peer.Observation } : replacement switch
            {
                "absent" => Array.Empty<DongleObservation>(),
                "foreign" => new[] { peer.Observation with { Target = peer.Target with { Identity = "foreign" } } },
                _ => new[] { peer.Observation, peer.Observation }
            };
            await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
            Assert.Equal(1, peer.Setters); Assert.True(peer.LeaseDisposed); Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed)); Assert.True(peer.Elapsed <= 15.1);
        }
    }

    [Fact]
    public async Task RemainingTotalBudgetCapsSlowStatusReads()
    {
        var peer = new DonglePeer(); peer.OnControlBudget = (_, remaining) => peer.Clock.Advance(TimeSpan.FromSeconds(Math.Min(0.5, remaining.TotalSeconds)));
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
        Assert.Equal(1, peer.Setters); Assert.True(peer.Elapsed <= 15.1); Assert.Contains(peer.ReadTimeouts, timeout => timeout < TimeSpan.FromSeconds(3)); Assert.True(peer.LeaseDisposed);
    }

    [Fact]
    public async Task OpenIdentityDescriptorAndInstanceChangesAreRejected()
    {
        var peer = new DonglePeer { InvalidDescriptor = true };
        await Assert.ThrowsAnyAsync<Exception>(() => peer.Complete(new DongleService(peer, peer.Clock).StatusAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(peer.LeaseDisposed); Assert.True(peer.Endpoints.Single().Disposed);
        peer = new DonglePeer(); peer.OnOpen = () => peer.Observation = peer.Observation with { DeviceNumber = 5 };
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).StatusAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(peer.Endpoints.Single().Disposed); Assert.Empty(peer.Writes);
        peer = new DonglePeer();
        peer.OnControl = command => { if (command == 4) peer.Observation = peer.Observation with { DeviceNumber = 5 }; if (peer.Setters > 0) peer.Active = 4; };
        peer.OnOpen = () => { if (peer.Endpoints.Count == 2) peer.Observation = peer.Observation with { DeviceNumber = 6 }; };
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(new DongleService(peer, peer.Clock).SetCodecAsync(peer.Target, DongleCodec.AptxAdaptive, TestContext.Current.CancellationToken)));
        Assert.Equal(2, peer.Endpoints.Count); Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed));
    }
}
