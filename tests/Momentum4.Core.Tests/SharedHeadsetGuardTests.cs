using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

public sealed class SharedHeadsetGuardTests
{
    [Fact(Timeout = 15000)]
    public async Task Existing_windows_action_rechecks_identity_and_denies_unknown_model()
    {
        var headset = new SimulatedHeadset();
        var unknown = false;
        var transport = new FakeTransport
        {
            Responder = bytes =>
            {
                var frame = new GaiaFrameReader().Push(bytes).Frames.Single();
                return unknown && frame.Command.Value == 0x1206
                    ? [GaiaFrameCodec.Encode(frame.Vendor, frame.Command.AsResponse(), System.Text.Encoding.UTF8.GetBytes("Foreign"))]
                    : headset.Respond(bytes);
            },
        };
        transport.SetState(TransportState.Disconnected);
        await using var service = new Momentum4Service(transport, CommandPolicy.Production,
            new Momentum4ServiceOptions { RegisterNotifications = false }, NullLoggerFactory.Instance);
        service.Start();
        for (var attempt = 0; attempt < 1000 && service.Store.Current.Connection != ConnectionState.Connected; attempt++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        unknown = true;
        await Assert.ThrowsAsync<FeatureNotSupportedException>(() => service.SetBehaviorAsync(BehaviorSetting.SmartPause, true, TestContext.Current.CancellationToken));
        Assert.Empty(headset.RequestsStartingWith("FF 03 00 01 04 95 08 0C"));
    }
}
