using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

internal sealed class DongleUpgradeSession(IDongleLease lease, DongleTarget target, DongleBudget budget) : IAsyncDisposable
{
    private IHidEndpoint? endpoint;
    private DongleObservation? observation;
    private bool connected;

    internal async Task ConnectAsync()
    {
        if (connected) throw new DongleException("Upgrade session is already connected");
        using var commandBudget = budget.Slice(6);
        if (endpoint is null)
        {
            observation = await DongleIo.ObserveAsync(lease, target, commandBudget, true).ConfigureAwait(false) ?? throw new DongleDisconnectedException("BTD 700 disconnected");
            endpoint = await DongleIo.OpenAsync(lease, observation, HidInterface.Upgrade, commandBudget).ConfigureAwait(false);
        }
        else if (await DongleIo.ObserveAsync(lease, observation!.Target, commandBudget, true).ConfigureAwait(false) != observation)
            throw new DongleException("USB instance changed before upgrade connection");
        if (await endpoint.SetFeatureAsync(DongleReports.FeatureRequest(HidFeatureOperation.Connect), commandBudget.Remaining, commandBudget.Token).ConfigureAwait(false) != 63)
            throw new DongleException("Incomplete BTD 700 feature report");
        DongleReports.ValidateConnection(await DongleIo.ReadAsync(endpoint, commandBudget).ConfigureAwait(false));
        connected = true;
    }

    internal async Task SendAsync(UpgradeOpcode opcode, byte[] payload)
    {
        if (!connected || endpoint is null) throw new DongleException("Upgrade session is not connected");
        using var commandBudget = budget.Slice(6);
        await DongleIo.WriteAsync(endpoint, DongleReports.UpgradeRequest(opcode, payload), commandBudget).ConfigureAwait(false);
    }

    internal async Task<(byte Opcode, byte[] Payload)> ReceiveAsync(params byte[] expected)
    {
        if (!connected || endpoint is null) throw new DongleException("Upgrade session is not connected");
        using var commandBudget = budget.Slice(6);
        return DongleReports.UpgradeReply(await DongleIo.ReadAsync(endpoint, commandBudget).ConfigureAwait(false), expected);
    }

    internal async Task<UpgradeVersion> GetVersionAsync()
    {
        await SendAsync(UpgradeOpcode.GetVersion, []).ConfigureAwait(false);
        var (_, payload) = await ReceiveAsync(0x1a).ConfigureAwait(false);
        return DongleReports.DecodeVersion(payload);
    }

    internal async Task ReopenAsync()
    {
        if (observation is null) throw new DongleException("Upgrade session has no bound USB instance");
        using var reopenBudget = budget.Slice(60);
        if (endpoint is not null) { await endpoint.DisposeAsync().ConfigureAwait(false); endpoint = null; }
        connected = false; var removed = false;
        while (true)
        {
            var current = await DongleIo.ObserveAsync(lease, target, reopenBudget).ConfigureAwait(false);
            if (current is null) removed = true;
            else
            {
                var changed = current.DeviceNumber != observation.DeviceNumber || ((current.NodeDevice != observation.NodeDevice || current.NodeInode != observation.NodeInode) && current.ChangeStamp != observation.ChangeStamp);
                if (removed || changed)
                {
                    endpoint = await DongleIo.OpenAsync(lease, current, HidInterface.Upgrade, reopenBudget).ConfigureAwait(false);
                    observation = current; return;
                }
            }
            await reopenBudget.DelayAsync(0.05).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var closing = endpoint; endpoint = null; var disconnect = connected; connected = false;
        if (closing is null) return;
        try
        {
            if (disconnect)
            {
                using var cleanup = new DongleBudget(budget.Clock, TimeSpan.FromSeconds(1), CancellationToken.None);
                try { await closing.SetFeatureAsync(DongleReports.FeatureRequest(HidFeatureOperation.Disconnect), cleanup.Remaining, cleanup.Token).ConfigureAwait(false); }
                catch (DongleException) { }
                catch (OperationCanceledException) { }
            }
        }
        finally { await closing.DisposeAsync().ConfigureAwait(false); }
    }
}
