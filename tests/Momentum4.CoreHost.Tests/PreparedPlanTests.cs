using Momentum4.CoreHost;

namespace Momentum4.CoreHost.Tests;

public sealed class PreparedPlanTests
{
    [Fact]
    public void SequentialHandlesReplaceAndConsumeOnce()
    {
        var slot = new PreparedPlanSlot<object>(TimeProvider.System);
        var first = slot.Replace(new object(), "A");
        var value = new object();
        var second = slot.Replace(value, "A");
        Assert.Equal(first + 1, second);
        Assert.Same(value, slot.Consume(second, "A"));
        Assert.Throws<PrivateProtocolException>(() => slot.Consume(second, "A"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleAndForeignHandlesRevokeSlot(bool foreignTarget)
    {
        var slot = new PreparedPlanSlot<object>(TimeProvider.System);
        var first = slot.Replace(new object(), "A");
        var current = slot.Replace(new object(), "A");
        Assert.Throws<PrivateProtocolException>(() => slot.Consume(foreignTarget ? current : first, foreignTarget ? "B" : "A"));
        Assert.Throws<PrivateProtocolException>(() => slot.Consume(current, "A"));
    }

    [Fact]
    public void MonotonicLifetimeExpiresAtTenMinutes()
    {
        var clock = new Clock();
        var slot = new PreparedPlanSlot<object>(clock);
        var value = new object();
        var before = slot.Replace(value, "A");
        clock.Tick = 599;
        Assert.Same(value, slot.Consume(before, "A"));
        var expired = slot.Replace(value, "A");
        clock.Tick += 600;
        Assert.Throws<PrivateProtocolException>(() => slot.Consume(expired, "A"));
    }

    private sealed class Clock : TimeProvider
    {
        public long Tick { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Tick;
    }
}
