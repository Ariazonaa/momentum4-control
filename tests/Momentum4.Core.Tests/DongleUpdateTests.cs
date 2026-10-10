using System.Reflection;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

public sealed class DongleUpdateTests
{
    [Fact]
    public async Task FreshTransactionTransfersExactBytesAndCommitsOnce()
    {
        using var prepared = await Prepare(); var progress = new List<DongleUpdateProgress>();
        var result = await prepared.Service.ApplyUpdateAsync(prepared.Plan, new ImmediateProgress(progress.Add), TestContext.Current.CancellationToken);
        Assert.Equal(new UpgradeVersion(new FirmwareVersion(4, 0, 0), 2), result);
        Assert.Equal(prepared.Bytes, prepared.Peer.Received.ToArray());
        Assert.Equal(1, prepared.Peer.Commits); Assert.True(prepared.Peer.Adapter.LeaseDisposed);
        Assert.Equal(new DongleUpdateProgress("complete", 100), progress[^1]);
        Assert.Equal(progress.Count, progress.Distinct().Count());
        Assert.All(prepared.Peer.Adapter.Endpoints, endpoint => Assert.True(endpoint.Disposed));
        Assert.Equal(new byte[] { 0x19, 0x19, 0x13, 1, 0x15, 4, 4, 4, 4, 0x16, 0x16, 0x0c, 0x13, 1, 0x0e, 0x10, 0x19 }, prepared.Peer.Adapter.Writes.Where(frame => frame[0] == 5).Select(frame => frame[2]));
    }

    [Fact]
    public async Task PreflightRejectsAudioUnknownStateForeignPlanAndChangedSource()
    {
        foreach (var state in new byte[] { 3, 4, 255 })
        {
            using var prepared = await Prepare(); prepared.Peer.Adapter.State = state;
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(error.RecoveryRequired); Assert.Equal("preflight", error.Stage); Assert.Equal(0, prepared.Peer.Syncs);
            Assert.Contains(state is 3 or 4 ? "Stop audio" : "Unknown dongle", error.Message);
        }
        using var source = await Prepare(); using var foreign = new DongleFirmwareService(source.Peer.Adapter, source.Peer.Adapter.Clock, new FirmwareHttp([]));
        var rejected = await Assert.ThrowsAsync<DongleUpdateException>(() => foreign.ApplyUpdateAsync(source.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(rejected.RecoveryRequired); Assert.Equal(0, source.Peer.Syncs);
        source.Peer.Source = new UpgradeVersion(new FirmwareVersion(3, 12, 0), 1);
        rejected = await Assert.ThrowsAsync<DongleUpdateException>(() => source.Service.ApplyUpdateAsync(source.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(rejected.RecoveryRequired); Assert.Equal(0, source.Peer.Syncs);
    }

    [Fact]
    public async Task EqualAndDowngradePreparedSourcesAreRejectedEvenWhenCompatible()
    {
        foreach (var current in new[] { new FirmwareVersion(4, 0, 0), new FirmwareVersion(5, 0, 0) })
        {
            using var prepared = await Prepare(broadCompatibility: true);
            var source = new UpgradeVersion(current, 1);
            typeof(DongleUpdatePlan).GetField("<Current>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(prepared.Plan, source);
            prepared.Peer.Source = source;
            var count = prepared.Peer.Adapter.Writes.Count;
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Contains("strictly newer", error.Message); Assert.Equal(count, prepared.Peer.Adapter.Writes.Count); Assert.False(error.RecoveryRequired);
        }
    }

    [Fact]
    public async Task PreparedDigestAndMetadataTamperingAreRejectedBeforeAnyWrite()
    {
        using var prepared = await Prepare();
        var imageField = typeof(DongleUpdatePlan).GetField("image", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var image = (FirmwareImage)imageField.GetValue(prepared.Plan)!;
        var bytes = (byte[])typeof(FirmwareImage).GetField("data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
        bytes[^1] ^= 1;
        var resume = (byte[])typeof(FirmwareImage).GetField("resumeId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
        FirmwareImage.Parse(bytes).ResumeId.CopyTo(resume, 0);
        var prior = prepared.Peer.Adapter.Writes.Count;
        var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(error.RecoveryRequired); Assert.Equal(prior, prepared.Peer.Adapter.Writes.Count);
    }

    [Fact]
    public async Task BadSyncCheckpointIdProtocolAndStartRefuseData()
    {
        foreach (var payload in new byte[][] { [1, 0, 0, 0, 0], [0, 0, 0, 0, 0, 1], [0], [0, 0, 0, 0, 0, 2] })
        {
            using var prepared = await Prepare(); prepared.Peer.Overrides[0x13] = (0x14, payload);
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(error.RecoveryRequired); Assert.Equal("sync", error.Stage); Assert.Empty(prepared.Peer.Received);
        }
        using var protocol = await Prepare(); protocol.Peer.Overrides[0x13] = (0x14, new byte[] { 0 }.Concat(FirmwareImage.Parse(protocol.Bytes).ResumeId).Concat(new byte[] { 2 }).ToArray());
        var protocolError = await Assert.ThrowsAsync<DongleUpdateException>(() => protocol.Service.ApplyUpdateAsync(protocol.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("protocol version", protocolError.Message); Assert.Empty(protocol.Peer.Received);
        using var start = await Prepare(); start.Peer.Overrides[1] = (2, [1, 0, 100]);
        var failure = await Assert.ThrowsAsync<DongleUpdateException>(() => start.Service.ApplyUpdateAsync(start.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("start", failure.Stage); Assert.Empty(start.Peer.Received);
    }

    [Fact]
    public async Task DataRejectsZeroOffsetOversizeShortAndPrematureCompletion()
    {
        foreach (var (opcode, payload) in new[] { ((byte)3, DongleFirmwareFixture.DataRequest(0)), ((byte)3, DongleFirmwareFixture.DataRequest(1, 1)), ((byte)3, DongleFirmwareFixture.DataRequest(99999)), ((byte)3, new byte[7]), ((byte)0x0b, Array.Empty<byte>()) })
        {
            using var prepared = await Prepare(); prepared.Peer.Overrides[0x15] = (opcode, payload);
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("transfer", error.Stage); Assert.True(error.RecoveryRequired); Assert.Empty(prepared.Peer.Received); Assert.Equal(0, prepared.Peer.Commits);
            if (opcode == 3 && payload.Length == 8) Assert.Contains("out-of-range", error.Message);
        }
    }

    [Fact]
    public async Task SegmentedRequestsOnlyFlagAbsoluteEofAndProgressIsBounded()
    {
        foreach (var requestSize in new[] { 1, 100 })
        {
            using var prepared = await Prepare(); prepared.Peer.RequestSize = requestSize; var progress = new List<DongleUpdateProgress>();
            await prepared.Service.ApplyUpdateAsync(prepared.Plan, new ImmediateProgress(progress.Add), TestContext.Current.CancellationToken);
            Assert.Equal(prepared.Bytes, prepared.Peer.Received.ToArray());
            var data = prepared.Peer.Adapter.Writes.Where(frame => frame[0] == 5 && frame[2] == 4).ToArray();
            Assert.All(data[..^1], frame => Assert.Equal((byte)0, frame[5])); Assert.Equal((byte)1, data[^1][5]);
            Assert.True(progress.Count <= 100); Assert.Equal(progress.Count, progress.Distinct().Count());
        }
    }

    [Fact]
    public async Task StagePayloadShapesAndUncertainCommitRetainRecoveryWithoutRetry()
    {
        foreach (var (request, opcode, payload, stage) in new[] { ((byte)1, (byte)2, new byte[2], "start"), ((byte)0x16, (byte)0x17, new byte[1], "validation"), ((byte)0x16, (byte)0x0b, new byte[1], "validation"), ((byte)0x0e, (byte)0x0f, new byte[1], "proceed"), ((byte)0x10, (byte)0x12, new byte[1], "commit") })
        {
            using var prepared = await Prepare(); prepared.Peer.Overrides[request] = (opcode, payload);
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(stage, error.Stage); Assert.True(error.RecoveryRequired); Assert.True(prepared.Peer.Commits <= 1); Assert.True(prepared.Peer.Adapter.LeaseDisposed);
        }
        using var lost = await Prepare(); lost.Peer.LostCommit = true;
        var failure = await Assert.ThrowsAsync<DongleUpdateException>(() => lost.Service.ApplyUpdateAsync(lost.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("commit", failure.Stage); Assert.True(failure.RecoveryRequired); Assert.Equal(1, lost.Peer.Commits);
    }

    [Fact]
    public async Task LegacyFiveByteSyncIsAccepted()
    {
        using var prepared = await Prepare(); prepared.Peer.LegacySync = true;
        await prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, prepared.Peer.Commits);
    }

    [Fact]
    public async Task EndlessValidationAndTinyRequestsAreBounded()
    {
        using var prepared = await Prepare(); prepared.Peer.Overrides[0x16] = (0x17, [0, 0]);
        var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("validation", error.Stage); Assert.Equal(256, prepared.Peer.ValidationPolls); Assert.True(error.RecoveryRequired);
        using var tiny = await Prepare(60000); tiny.Peer.RequestSize = 1;
        error = await Assert.ThrowsAsync<DongleUpdateException>(() => tiny.Service.ApplyUpdateAsync(tiny.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("transfer", error.Stage); Assert.Contains("packet budget", error.Message); Assert.True(tiny.Peer.Received.Count < tiny.Bytes.Length); Assert.Equal(0, tiny.Peer.Commits); Assert.Equal(100006, tiny.Peer.Adapter.UpgradeReads + tiny.Peer.Adapter.Writes.Count(frame => frame[0] == 5));
    }

    [Fact]
    public async Task CancellationBeforeSyncAndDuringTransferHaveTruthfulRecovery()
    {
        using var prepared = await Prepare(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var count = prepared.Peer.Adapter.Writes.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: cancellation.Token));
        Assert.Equal(count, prepared.Peer.Adapter.Writes.Count);
        using var transfer = await Prepare(); using var stop = new CancellationTokenSource(); transfer.Peer.OnData = () => stop.Cancel();
        var error = await Assert.ThrowsAsync<DongleUpdateException>(() => transfer.Service.ApplyUpdateAsync(transfer.Plan, cancellationToken: stop.Token));
        Assert.True(error.RecoveryRequired); Assert.True(error.Interrupted); Assert.Equal("transfer", error.Stage); Assert.True(transfer.Peer.Adapter.LeaseDisposed);
    }

    [Fact]
    public async Task DeadlineAndFalseRebootRejectPathOrCtimeOnlyChanges()
    {
        foreach (var change in new[] { "none", "path", "ctime", "foreign", "ambiguous" })
        {
            using var prepared = await Prepare(); prepared.Peer.RebootChange = change;
            var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Peer.Adapter.Complete(prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken)));
            Assert.Equal("reboot", error.Stage); Assert.True(error.RecoveryRequired); Assert.Equal(0, prepared.Peer.Commits); Assert.True(prepared.Peer.Adapter.Elapsed <= 60.1);
        }
        using var timeout = await Prepare(); timeout.Peer.OnData = () => timeout.Peer.Adapter.Clock.Advance(TimeSpan.FromSeconds(600));
        var failure = await Assert.ThrowsAsync<DongleUpdateException>(() => timeout.Service.ApplyUpdateAsync(timeout.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("transfer", failure.Stage); Assert.Equal(0, timeout.Peer.Commits);
    }

    [Fact]
    public async Task FinalVersionAndPostRebootCheckpointMustMatch()
    {
        using var prepared = await Prepare(); prepared.Peer.FinalVersion = new UpgradeVersion(new FirmwareVersion(4, 0, 1), 2);
        var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("final version", error.Stage); Assert.True(error.RecoveryRequired); Assert.Equal(1, prepared.Peer.Commits);
        using var checkpoint = await Prepare(); checkpoint.Peer.PostCheckpoint = 2;
        error = await Assert.ThrowsAsync<DongleUpdateException>(() => checkpoint.Service.ApplyUpdateAsync(checkpoint.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("post-reboot sync", error.Stage); Assert.Equal(0, checkpoint.Peer.Commits);
    }

    [Fact]
    public async Task CleanupInterruptionRetainsStageAndRecovery()
    {
        using var prepared = await Prepare(); prepared.Peer.Overrides[0x15] = (3, new byte[7]);
        prepared.Peer.Adapter.OnClose = kind => kind == HidInterface.Upgrade ? new OperationCanceledException("cleanup interrupted") : null;
        var error = await Assert.ThrowsAsync<DongleUpdateException>(() => prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("transfer", error.Stage); Assert.True(error.RecoveryRequired); Assert.True(error.Interrupted); Assert.True(prepared.Peer.Adapter.LeaseDisposed);
    }

    [Fact]
    public async Task NewUsbNodeAndObservedRemovalAreValidRebootEvidence()
    {
        using var prepared = await Prepare(); prepared.Peer.RebootChange = "node";
        await prepared.Service.ApplyUpdateAsync(prepared.Plan, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, prepared.Peer.Commits);
        using var removed = await Prepare(); removed.Peer.RebootChange = "none"; var absent = false;
        removed.Peer.Adapter.OnObserve = () =>
        {
            if (removed.Peer.Adapter.Writes.Any(frame => frame[0] == 5 && frame[2] == 0x0c) && !absent) { absent = true; return Array.Empty<DongleObservation>(); }
            return new[] { removed.Peer.Adapter.Observation };
        };
        await removed.Peer.Adapter.Complete(removed.Service.ApplyUpdateAsync(removed.Plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(absent); Assert.Equal(1, removed.Peer.Commits);
    }

    private static async Task<Prepared> Prepare(int payloadSize = 600, bool broadCompatibility = false)
    {
        var peer = new FirmwareTransactionPeer(); var bytes = DongleFirmwareFixture.Image(payloadSize, broadCompatibility ? [0x0003ffff, 0x0004ffff, 0x0005ffff] : null); peer.ImageSize = bytes.Length;
        var service = new DongleFirmwareService(peer.Adapter, peer.Adapter.Clock, new FirmwareHttp(DongleFirmwareTests.Responses(bytes)));
        var plan = await service.CheckUpdateAsync(peer.Adapter.Target, TestContext.Current.CancellationToken);
        return new Prepared(service, plan, peer, bytes);
    }
    private sealed record Prepared(DongleFirmwareService Service, DongleUpdatePlan Plan, FirmwareTransactionPeer Peer, byte[] Bytes) : IDisposable { public void Dispose() => Service.Dispose(); }
    private sealed class ImmediateProgress(Action<DongleUpdateProgress> report) : IProgress<DongleUpdateProgress> { public void Report(DongleUpdateProgress value) => report(value); }
}
