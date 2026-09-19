// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol.Catalog;

namespace Momentum4.Core.Tests;

/// <summary>Multipoint: gekoppeltes Gerät verbinden (docs/architecture.md §8.5) gegen das simulierte Headset.</summary>
public sealed class MultipointActionTests
{
    private const int TestTimeoutMs = 15_000;

    private const string ConnectPrefix = "FF 03 00 01 04 95 14 02";

    private static readonly CommandPolicy SetterPolicy = CommandPolicy.ForHardwareTest(CommandCatalog.ConnectPairedDevice, CommandCatalog.GetPairedDeviceStatus);

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();

    public MultipointActionTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Connects_peer_and_verifies_that_this_pc_stays_connected()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken);

        Assert.Equal(["FF 03 00 01 04 95 14 02 02"], _headset.RequestsStartingWith(ConnectPrefix));
        Assert.True(result.Devices.Single(d => d.Name == "Phone-C").Connected);
        Assert.True(result.Devices.Single(d => d.IsThisComputer).Connected);
        Assert.True(service.Store.Current.Multipoint!.Devices[2].Connected);
        AssertNeverDisconnected();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Refuses_when_the_name_at_the_index_changed()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);
        _headset.PeerNames[2] = "Laptop-X"; // Liste hat sich seit der Anzeige geändert

        await Assert.ThrowsAsync<PeerActionRefusedException>(() => service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith(ConnectPrefix));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Refuses_this_pc_and_unknown_indexes()
    {
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PeerActionRefusedException>(() => service.ConnectPeerAsync(0, "PC-A", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PeerActionRefusedException>(() => service.ConnectPeerAsync(7, "Phone-C", TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith(ConnectPrefix));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Already_connected_peer_sends_nothing()
    {
        _headset.PeerConnected[2] = true;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var result = await service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken);

        Assert.True(result.Devices[2].Connected);
        Assert.Empty(_headset.RequestsStartingWith(ConnectPrefix));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Refuses_when_the_maximum_number_of_connections_is_reached()
    {
        _headset.PeerConnected[1] = true; // PC + Laptop = 2 von 2
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PeerActionRefusedException>(() => service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith(ConnectPrefix));
        AssertNeverDisconnected();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Headset_error_is_reported_without_retry()
    {
        _headset.RejectConnectStatus = 0x01;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<GaiaErrorException>(() => service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken));

        Assert.Equal((byte)0x01, error.Reason);
        Assert.Single(_headset.RequestsStartingWith(ConnectPrefix));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Peer_that_never_connects_is_reported_after_the_timeout_without_retry()
    {
        _headset.PeersNeverConnect = true;
        await using var service = await StartAsync(SetterPolicy, TestContext.Current.CancellationToken);

        var action = service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken);
        while (!action.IsCompleted)
        {
            _time.Advance(TimeSpan.FromMilliseconds(300));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<SettingNotAppliedException<string>>(() => action);
        Assert.Single(_headset.RequestsStartingWith(ConnectPrefix));
        Assert.InRange(_headset.RequestsStartingWith("FF 03 00 01 04 95 14 04 02").Count, 2, 40); // 8 s alle 300 ms
        Assert.False(service.Store.Current.Multipoint!.Devices[2].Connected);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Production_allows_connecting_verified_on_hardware()
    {
        await using var service = await StartAsync(CommandPolicy.Production, TestContext.Current.CancellationToken);

        var result = await service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken);

        Assert.True(result.Devices[2].Connected);
        AssertNeverDisconnected();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Policy_without_connect_sends_nothing()
    {
        var policy = CommandPolicy.Only([.. CommandCatalog.All.Where(d => d.Safety == SafetyClass.Read && d.Status == ProtocolStatus.Verified)]);
        await using var service = await StartAsync(policy, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<CommandNotAllowedException>(() => service.ConnectPeerAsync(2, "Phone-C", TestContext.Current.CancellationToken));

        Assert.Empty(_headset.RequestsStartingWith(ConnectPrefix));
    }

    [Fact]
    public void Disconnect_stays_blocked_in_every_policy()
    {
        Assert.Equal(SafetyClass.Blocked, CommandCatalog.DisconnectPairedDevice.Safety);
        Assert.False(CommandPolicy.Production.IsAllowed(CommandCatalog.DisconnectPairedDevice, out _));
        Assert.Throws<ArgumentException>(() => CommandPolicy.ForHardwareTest(CommandCatalog.DisconnectPairedDevice));
        Assert.Throws<ArgumentException>(() => CommandPolicy.Only(CommandCatalog.DisconnectPairedDevice));
    }

    private async Task<Momentum4Service> StartAsync(CommandPolicy policy, CancellationToken cancellationToken)
    {
        var service = new Momentum4Service(_transport, policy, new Momentum4ServiceOptions { RegisterNotifications = false }, NullLoggerFactory.Instance, _time);
        service.Start();
        for (var i = 0; i < 1000 && service.Store.Current.Connection != ConnectionState.Connected; i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        Assert.NotNull(service.Store.Current.Multipoint);
        return service;
    }

    private void AssertNeverDisconnected() => Assert.Empty(_headset.RequestsStartingWith("FF 03 00 01 04 95 14 03"));
}
