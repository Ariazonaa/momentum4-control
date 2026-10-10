using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Momentum4.Core.Dongle;
using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Tests;

public sealed class DongleFirmwareTests
{
    [Fact]
    public async Task EqualVersionDoesNotDownloadOrStart()
    {
        var peer = VersionPeer(); var http = new FirmwareHttp([Json(new[] { "3.11.0" })]);
        using var service = new DongleFirmwareService(peer, peer.Clock, http);
        var plan = await service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken);
        Assert.False(plan.Available); Assert.Null(plan.ImageSize); Assert.Single(http.Requests);
        Assert.Equal(1, peer.Writes.Count(frame => frame[0] == 5)); Assert.True(peer.LeaseDisposed);
    }

    [Fact]
    public async Task PreparationBindsExactSourceTargetImmutableImageAndOfficialProvenance()
    {
        var peer = VersionPeer(); var image = DongleFirmwareFixture.Image(); var http = new FirmwareHttp(Responses(image));
        using var service = new DongleFirmwareService(peer, peer.Clock, http);
        var plan = await service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken);
        Assert.True(plan.Available); Assert.Equal(new FirmwareVersion(4, 0, 0), plan.TargetVersion);
        Assert.Equal(image.Length, plan.ImageSize); Assert.Equal(peer.Target, plan.Target);
        Assert.Equal(new UpgradeVersion(new FirmwareVersion(3, 11, 0), 1), plan.Current);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(), plan.Sha256);
        Assert.Empty(typeof(DongleUpdatePlan).GetConstructors());
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task PreparationRequiresMatchingImageSizeVersionAndCompatibility()
    {
        foreach (var change in new[] { "version", "config", "compatibility", "size" })
        {
            var image = DongleFirmwareFixture.Image();
            if (change == "version") image[20] = 0;
            if (change == "version") image[21] = 5;
            if (change == "config") image[34] = 0;
            if (change == "config") image[35] = 0;
            if (change == "compatibility") image[27] = 2;
            var peer = VersionPeer(); using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp(Responses(image, size: change == "size" ? "1" : null)));
            await Assert.ThrowsAnyAsync<Exception>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UrlPolicyRejectsEscapeRoutesBeforeImageRequest()
    {
        const string good = "https://firmware.s-consumer-cloud.com/files/btd700/4.0.0/image.bin";
        foreach (var url in new[] { good.Replace("https:", "http:"), good.Replace(".com/", ".com.evil/"), good.Replace(".com/", ".com:444/"), good.Replace(".com/", "@evil/"), good + "?x=1", good.Replace("/4.0.0/", "/3.0.0/"), good.Replace("image.bin", "../image.bin"), good.Replace("image.bin", "%2e%2e/image.bin") })
        {
            var peer = VersionPeer(); var http = new FirmwareHttp(Responses(DongleFirmwareFixture.Image(), url));
            using var service = new DongleFirmwareService(peer, peer.Clock, http);
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
            Assert.Equal(2, http.Requests.Count);
        }
    }

    [Fact]
    public async Task ReleaseListRequiresBoundedUniqueCanonicalVersions()
    {
        foreach (var releases in new object[] { Array.Empty<string>(), Enumerable.Range(0, 33).Select(index => $"0.0.{index}").ToArray(), new[] { "4.0.0", "4.0.0" }, new[] { "4.0.100" }, new[] { "4.0.0/evil" }, new[] { "04.0.0" }, new[] { "65536.0.0" }, "4.0.0" })
        {
            var peer = VersionPeer(); using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([Json(releases)]));
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task MetadataRejectsDuplicateKeysFailureWrongKindsAndExcessiveDepth()
    {
        foreach (var json in new[] { "{\"success\":false,\"success\":true,\"data\":[\"4.0.0\"]}", "{\"success\":1,\"data\":[\"4.0.0\"]}", "[]", "{\"success\":true,\"data\":[\"4.0.0\"],\"x\":{\"a\":1,\"a\":2}}", "{\"success\":true,\"data\": [" + new string('[', 30) + "0" + new string(']', 30) + "]}" })
        {
            var peer = VersionPeer(); using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([Encoding.UTF8.GetBytes(json)]));
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ManifestRejectsDuplicateFullPackagesWrongVersionAndInvalidSizes()
    {
        foreach (var size in new object[] { -1, 0, 16 * 1024 * 1024 + 1, true, "-1", "1.0", "000000001" })
        {
            var peer = VersionPeer(); var http = new FirmwareHttp(Responses(DongleFirmwareFixture.Image(), size: size));
            using var service = new DongleFirmwareService(peer, peer.Clock, http);
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        }
        foreach (var manifest in new object[] { new { systemRelease = "5.0.0", packages = new[] { Package(DongleFirmwareFixture.Image().Length) } }, new { systemRelease = "4.0.0", packages = new[] { Package(DongleFirmwareFixture.Image().Length), Package(DongleFirmwareFixture.Image().Length) } }, new { systemRelease = "4.0.0", packages = new[] { Package(DongleFirmwareFixture.Image().Length, version: "5.0.0") } }, new { systemRelease = "4.0.0", packages = new object[] { 1 } } })
        {
            var peer = VersionPeer(); using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([Json(new[] { "4.0.0" }), Json(manifest), DongleFirmwareFixture.Image()]));
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task NetworkRejectsRedirectEncodingOversizeAndTruncatedBody()
    {
        foreach (var response in new[] {
            new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent(Json(new[] { "3.11.0" })) },
            Response(new byte[256 * 1024 + 1]), Response(Encoding.UTF8.GetBytes("{}"), length: 9),
            Response([], length: 256 * 1024 + 1), Response(Json(new[] { "3.11.0" }), encoding: "gzip") })
        {
            var peer = VersionPeer(); var http = new FirmwareHttp([]) { ResponseOverride = response };
            using var service = new DongleFirmwareService(peer, peer.Clock, http);
            await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
            Assert.Single(http.Requests);
        }
    }

    [Fact]
    public async Task OversizedDeclaredBodyIsRejectedBeforeReading()
    {
        var peer = VersionPeer(); var body = new FirmwareBody(Json(new[] { "3.11.0" }));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentLength = 256 * 1024 + 1;
        using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([]) { ResponseOverride = response });
        await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        Assert.Equal(0, body.BytesRead); Assert.True(body.Closed);
    }

    [Fact]
    public async Task StreamingBodyCapAndDeadlineCloseTheOwnedResponse()
    {
        var oversized = Enumerable.Repeat((byte)' ', 256 * 1024 + 1).ToArray();
        Json(new[] { "3.11.0" }).CopyTo(oversized, 0);
        var peer = VersionPeer(); var body = new FirmwareBody(oversized);
        var http = new FirmwareHttp([]) { ResponseOverride = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) } };
        using var service = new DongleFirmwareService(peer, peer.Clock, http);
        await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        Assert.Equal(256 * 1024 + 1, body.BytesRead); Assert.True(body.Closed);
        peer = VersionPeer(); body = new FirmwareBody([]) { Stall = true };
        http = new FirmwareHttp([]) { ResponseOverride = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) } };
        using var stalledService = new DongleFirmwareService(peer, peer.Clock, http);
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(stalledService.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(body.CancellationObserved); Assert.True(body.Closed); Assert.True(peer.Elapsed >= 60 && peer.Elapsed <= 60.1);
    }

    [Fact]
    public async Task FeatureAndWaitingUpgradeReplyFailuresCloseTheEndpoint()
    {
        var peer = VersionPeer(); peer.FailFeature = true;
        using var service = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([]));
        await Assert.ThrowsAsync<DongleException>(() => service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken));
        Assert.True(peer.LeaseDisposed); Assert.True(peer.Endpoints.Single().Disposed);
        peer = VersionPeer(); peer.OnUpgrade = (_, _) => { peer.StallRead = true; return null; };
        using var waitingService = new DongleFirmwareService(peer, peer.Clock, new FirmwareHttp([]));
        await Assert.ThrowsAnyAsync<Exception>(() => peer.Complete(waitingService.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(peer.Elapsed >= 6 && peer.Elapsed <= 6.1); Assert.True(peer.LeaseDisposed); Assert.All(peer.Endpoints, endpoint => Assert.True(endpoint.Disposed));
        Assert.All(peer.ReadTimeouts, timeout => Assert.True(timeout <= TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public async Task PreparationBudgetIsNotRenewedAcrossMetadataAndImageRequests()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), DongleFirmwareService.PreparationTimeout);
        var peer = VersionPeer(); var image = DongleFirmwareFixture.Image(); var http = new FirmwareHttp(Responses(image));
        http.DelayRequest = token => Task.Delay(TimeSpan.FromSeconds(http.Requests.Count == 3 ? 20 : 40), peer.Clock, token);
        using var service = new DongleFirmwareService(peer, peer.Clock, http);
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.Equal(3, http.Requests.Count); Assert.True(peer.Elapsed >= 90 && peer.Elapsed <= 90.1); Assert.True(peer.LeaseDisposed);
    }

    [Fact]
    public async Task AbsoluteDownloadBudgetAndCancellationInterruptHeaders()
    {
        var peer = VersionPeer(); var http = new FirmwareHttp([]) { StallHeaders = true };
        using var service = new DongleFirmwareService(peer, peer.Clock, http);
        await Assert.ThrowsAsync<DongleException>(() => peer.Complete(service.CheckUpdateAsync(peer.Target, TestContext.Current.CancellationToken)));
        Assert.True(http.CancellationObserved); Assert.True(peer.Elapsed <= 60.1); Assert.True(peer.LeaseDisposed);
        peer = VersionPeer(); http = new FirmwareHttp([]) { StallHeaders = true }; using var cancellation = new CancellationTokenSource();
        using var cancelledService = new DongleFirmwareService(peer, peer.Clock, http);
        var task = cancelledService.CheckUpdateAsync(peer.Target, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); Assert.True(http.CancellationObserved);
    }

    internal static DonglePeer VersionPeer()
    {
        var peer = new DonglePeer { State = 2 };
        peer.OnUpgrade = (opcode, _) => opcode == 0x19 ? DonglePeer.UpgradeReply(0x1a, [0, 3, 4, 0x4c, 0, 1]) : throw new InvalidOperationException();
        return peer;
    }
    internal static byte[][] Responses(byte[] image, string? url = null, object? size = null) => [Json(new[] { "3.11.0", "4.0.0" }), Json(new { systemRelease = "4.0.0", packages = new[] { Package(size ?? image.Length.ToString(), url) } }), image];
    private static object Package(object size, string? url = null, string version = "4.0.0") => new { type = "FW_FULL", identifier = "0x0000", version, size, url = url ?? "https://firmware.s-consumer-cloud.com/files/btd700/4.0.0/image.bin" };
    private static byte[] Json(object data) => JsonSerializer.SerializeToUtf8Bytes(new { success = true, data });
    private static HttpResponseMessage Response(byte[] bytes, long? length = null, string? encoding = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (length is not null) response.Content.Headers.ContentLength = length;
        if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
        return response;
    }
}

internal sealed class FirmwareHttp(IEnumerable<byte[]> responses) : HttpMessageHandler
{
    private readonly Queue<byte[]> responses = new(responses);
    internal List<Uri> Requests { get; } = [];
    internal HttpResponseMessage? ResponseOverride { get; set; }
    internal bool StallHeaders { get; set; }
    internal Func<CancellationToken, Task>? DelayRequest { get; set; }
    internal bool CancellationObserved { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        if (DelayRequest is not null) await DelayRequest(cancellationToken);
        if (StallHeaders)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        return ResponseOverride ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responses.Dequeue()) };
    }
}

internal sealed class FirmwareBody(byte[] data) : Stream
{
    internal int BytesRead { get; private set; }
    internal bool Closed { get; private set; }
    internal bool Stall { get; set; }
    internal bool CancellationObserved { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Stall)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        var count = Math.Min(buffer.Length, data.Length - BytesRead); data.AsMemory(BytesRead, count).CopyTo(buffer); BytesRead += count; return count;
    }
    protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
