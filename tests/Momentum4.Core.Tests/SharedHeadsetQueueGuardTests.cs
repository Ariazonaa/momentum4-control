using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetQueueGuardTests
{
    [Fact(Timeout = 15000)]
    public async Task Applied_write_with_nonempty_ack_is_rejected_before_readback_success()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x080c)
            {
                fixture.Headset.SmartPause = true;
                fixture.Overrides[0x080c] = [1];
            }
        };
        await Assert.ThrowsAsync<ProtocolFormatException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken));
        Assert.True(fixture.Headset.SmartPause);
        Assert.Single(fixture.Requests, frame => frame.Command.Value == 0x080c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0001")]
    public async Task Malformed_error_payload_is_format_failure(string payload)
    {
        var transport = new FakeTransport
        {
            Responder = bytes =>
            {
                var frame = new GaiaFrameReader().Push(bytes).Frames.Single();
                return [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsError(), Hex.Parse(payload))];
            },
        };
        using var queue = new GaiaCommandQueue(transport, CommandPolicy.Production, NullLogger<GaiaCommandQueue>.Instance);
        await Assert.ThrowsAsync<ProtocolFormatException>(() => queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken));
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task Strict_queue_rejects_invalid_prefix_even_with_following_matching_answer()
    {
        var transport = new FakeTransport
        {
            Responder = bytes =>
            {
                var request = new GaiaFrameReader().Push(bytes).Frames.Single();
                return [new byte[] { 0 }.Concat(GaiaFrameCodec.Encode(request.Vendor, request.Command.AsResponse(), new byte[] { 80 })).ToArray()];
            },
        };
        using var queue = new GaiaCommandQueue(transport, CommandPolicy.Production, NullLogger<GaiaCommandQueue>.Instance, strictFrames: true);
        await Assert.ThrowsAsync<ProtocolFormatException>(() => queue.SendAsync(CommandCatalog.GetBatteryLevel, TestContext.Current.CancellationToken));
    }
    [Fact(Timeout = 15000)]
    public async Task Disconnect_after_final_reply_does_not_publish_verified_snapshot()
    {
        var fixture = new SharedHeadsetFixture();
        await using var service = await fixture.StartAsync();
        await service.GetControlSnapshotAsync(TestContext.Current.CancellationToken);
        fixture.OnRequest = frame =>
        {
            if (frame.Command.Value == 0x0802 && fixture.Writes.Count > 0)
            {
                fixture.Transport.AfterResponse = () => fixture.Transport.SetState(Momentum4.Core.Transport.TransportState.Lost);
            }
        };
        await Assert.ThrowsAsync<Momentum4.Core.Transport.HeadsetUnavailableException>(() => service.SetBooleanControlAsync(HeadsetBooleanControl.SmartPause, true, TestContext.Current.CancellationToken));
        Assert.Equal(ConnectionState.Disconnected, service.Store.Current.Connection);
        Assert.Single(fixture.Writes);
    }

}
