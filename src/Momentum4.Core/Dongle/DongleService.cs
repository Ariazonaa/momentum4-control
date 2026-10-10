using Momentum4.Protocol.Dongle;

namespace Momentum4.Core.Dongle;

public sealed class DongleService(IDongleLeaseProvider leases, TimeProvider? timeProvider = null)
{
    public static TimeSpan StatusTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan CodecTimeout { get; } = TimeSpan.FromSeconds(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DongleStatus> StatusAsync(DongleTarget target, CancellationToken cancellationToken = default)
    {
        using var budget = new DongleBudget(clock, StatusTimeout, cancellationToken);
        try
        {
            await using var lease = await leases.AcquireAsync(target, budget.Remaining, budget.Token).ConfigureAwait(false);
            if (lease.Target != target) throw new DongleException("BTD 700 lease identity mismatch");
            var observation = await DongleIo.ObserveAsync(lease, target, budget, true).ConfigureAwait(false) ?? throw new DongleDisconnectedException("BTD 700 disconnected");
            await using var endpoint = await DongleIo.OpenAsync(lease, observation, HidInterface.Control, budget).ConfigureAwait(false);
            var status = await DongleIo.StatusAsync(endpoint, target, budget).ConfigureAwait(false);
            if (await DongleIo.ObserveAsync(lease, target, budget, true).ConfigureAwait(false) != observation) throw new DongleException("BTD 700 USB instance changed during status");
            return status;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested) { throw new DongleException("BTD 700 operation timed out", exception); }
    }

    public async Task<DongleStatus> SetCodecAsync(DongleTarget target, DongleCodec codec, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(codec)) throw new DongleException("Invalid BTD 700 codec selection");
        using var budget = new DongleBudget(clock, CodecTimeout, cancellationToken);
        IHidEndpoint? endpoint = null; var attempted = false;
        await using var lease = await leases.AcquireAsync(target, budget.Remaining, budget.Token).ConfigureAwait(false);
        try
        {
            if (lease.Target != target) throw new DongleException("BTD 700 lease identity mismatch");
            var observation = await DongleIo.ObserveAsync(lease, target, budget, true).ConfigureAwait(false) ?? throw new DongleDisconnectedException("BTD 700 disconnected");
            endpoint = await DongleIo.OpenAsync(lease, observation, HidInterface.Control, budget).ConfigureAwait(false);
            var status = await DongleIo.StatusAsync(endpoint, target, budget).ConfigureAwait(false);
            if ((status.SupportedCodecMask & (int)codec) == 0) throw new DongleException("BTD 700 codec is not supported");
            if (status.AudioMode != 0 || status.State is not (2 or 3)) throw new DongleException("BTD 700 codec requires standard mode and a connected audio-safe state");
            if (await DongleIo.ObserveAsync(lease, target, budget, true).ConfigureAwait(false) != observation) throw new DongleException("BTD 700 identity or USB instance changed before codec selection");
            budget.Check();
            if (status.CodecMask == (int)codec) return status;
            attempted = true;
            try
            {
                var acknowledgement = await DongleIo.ControlAsync(endpoint, 4, [(byte)codec, 0], [1], budget).ConfigureAwait(false);
                if (acknowledgement[0] != 0) throw new DongleException("BTD 700 codec selection rejected");
            }
            catch (DongleDisconnectedException) { await endpoint.DisposeAsync().ConfigureAwait(false); endpoint = null; }
            long? stableSince = null;
            while (true)
            {
                budget.Check();
                try
                {
                    var current = await DongleIo.ObserveAsync(lease, target, budget).ConfigureAwait(false);
                    if (current is null)
                    {
                        if (endpoint is not null) { await endpoint.DisposeAsync().ConfigureAwait(false); endpoint = null; }
                        stableSince = null; await budget.DelayAsync(0.1).ConfigureAwait(false); continue;
                    }
                    if (endpoint is not null && current != observation)
                    { await endpoint.DisposeAsync().ConfigureAwait(false); endpoint = null; stableSince = null; }
                    if (endpoint is null) { endpoint = await DongleIo.OpenAsync(lease, current, HidInterface.Control, budget).ConfigureAwait(false); observation = current; }
                    status = await DongleIo.StatusAsync(endpoint, current.Target, budget).ConfigureAwait(false);
                    if (await DongleIo.ObserveAsync(lease, target, budget).ConfigureAwait(false) != observation)
                    { stableSince = null; await budget.DelayAsync(0.1).ConfigureAwait(false); continue; }
                }
                catch (DongleDisconnectedException)
                {
                    if (endpoint is not null) { await endpoint.DisposeAsync().ConfigureAwait(false); endpoint = null; }
                    stableSince = null; await budget.DelayAsync(0.1).ConfigureAwait(false); continue;
                }
                if (status.CodecMask == (int)codec && status.State is 2 or 3 && status.AudioMode == 0)
                {
                    var now = clock.GetTimestamp();
                    if (stableSince is null) stableSince = now;
                    else if (clock.GetElapsedTime(stableSince.Value, now) >= TimeSpan.FromMilliseconds(500)) return status;
                }
                else stableSince = null;
                await budget.DelayAsync(0.1).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new DongleException(attempted ? "BTD 700 operation timed out; codec change was attempted but the active codec is unconfirmed" : "BTD 700 operation timed out", exception); }
        catch (Exception exception) when (attempted && exception is not OperationCanceledException)
        { throw new DongleException($"{exception.Message}; codec change was attempted but the active codec is unconfirmed", exception); }
        finally { if (endpoint is not null) await endpoint.DisposeAsync().ConfigureAwait(false); }
    }
}
