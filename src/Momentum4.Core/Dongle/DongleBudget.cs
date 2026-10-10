namespace Momentum4.Core.Dongle;

internal sealed class DongleBudget : IDisposable
{
    private readonly long started;
    private readonly TimeSpan duration;
    private readonly CancellationTokenSource expiry;
    private readonly CancellationTokenSource linked;
    internal TimeProvider Clock { get; }
    internal CancellationToken Token => linked.Token;
    internal DongleBudget(TimeProvider clock, TimeSpan duration, CancellationToken token)
    {
        Clock = clock; this.duration = duration; started = clock.GetTimestamp();
        expiry = new CancellationTokenSource(duration, clock); linked = CancellationTokenSource.CreateLinkedTokenSource(token, expiry.Token);
    }
    internal TimeSpan Remaining
    {
        get
        {
            Token.ThrowIfCancellationRequested();
            var remaining = duration - Clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new DongleException("BTD 700 operation timed out");
            return remaining;
        }
    }
    internal void Check() => _ = Remaining;
    internal DongleBudget Slice(double seconds) => new(Clock, TimeSpan.FromSeconds(Math.Min(seconds, Remaining.TotalSeconds)), Token);
    internal async Task DelayAsync(double seconds)
    {
        var remaining = Remaining;
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, remaining.TotalSeconds)), Clock, Token).ConfigureAwait(false);
        Check();
    }
    public void Dispose() { linked.Dispose(); expiry.Dispose(); }
}
