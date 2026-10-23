using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The backoff arithmetic, pinned: the two shipped schedules, the ceiling, the
/// degenerate multipliers, and the counting helper.
/// <para>
/// This is pure arithmetic with no clock and no await — which is exactly why it is
/// worth testing. The failure it guards against is a schedule that grows without
/// bound (a customer's laptop pinging a silent server all afternoon) or one that
/// never grows (a phone-scan wait hammering at one request per second). Neither is
/// visible in a test that sleeps.
/// </para>
/// </summary>
public sealed class PollScheduleTests
{
    [Fact]
    public void TheFirstAttemptWaitsExactlyTheFirstDelay()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), PollSchedule.Login.DelayBefore(0));
        Assert.Equal(TimeSpan.FromSeconds(5), PollSchedule.Activation.DelayBefore(0));
    }

    /// <summary>
    /// The login schedule settles at its three-second ceiling in about ten seconds,
    /// so a user fumbling with their phone leaves the app quiet rather than loud:
    /// 500ms, 750ms, 1.125s, 1.6875s, 2.53125s, then three seconds forever. The
    /// ceiling is reached at attempt 5 and held from there.
    /// </summary>
    [Fact]
    public void TheLoginScheduleGrowsUntilTheCeilingAndStopsThere()
    {
        var schedule = PollSchedule.Login;

        Assert.Equal(TimeSpan.FromMilliseconds(750), schedule.DelayBefore(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1125), schedule.DelayBefore(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1687.5), schedule.DelayBefore(3));
        Assert.Equal(TimeSpan.FromMilliseconds(2531.25), schedule.DelayBefore(4));
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.DelayBefore(5));
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.DelayBefore(50));
        // Far past the point where the naive multiply-out would overflow TimeSpan.
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.DelayBefore(10_000));
    }

    /// <summary>
    /// The activation wait is longer to begin with and grows more slowly, because
    /// the thing being waited for is a human reading a payment notification.
    /// </summary>
    [Fact]
    public void TheActivationScheduleStartsHigherAndCeilingsAtTenSeconds()
    {
        var schedule = PollSchedule.Activation;

        Assert.Equal(7000, schedule.DelayBefore(1).TotalMilliseconds, 3);
        Assert.Equal(9800, schedule.DelayBefore(2).TotalMilliseconds, 3);
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(100));
    }

    /// <summary>
    /// A multiplier at or below one means "no growth" — a constant poll — which is
    /// the shape a caller gets when it passes a schedule it built by hand. It must
    /// not silently become zero, and it must not grow.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void AMultiplierThatDoesNotGrowKeepsTheFirstDelay(double multiplier)
    {
        var schedule = new PollSchedule(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), multiplier);

        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(0));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(500));
    }

    /// <summary>
    /// A non-positive first delay is a caller that wants "ask immediately, every
    /// time" — zero, not a negative wait, and not the ceiling.
    /// </summary>
    [Fact]
    public void ANonPositiveFirstDelayWaitsNothingRatherThanGoingBackwards()
    {
        var zero = new PollSchedule(TimeSpan.Zero, TimeSpan.FromSeconds(10), 1.5);
        Assert.Equal(TimeSpan.Zero, zero.DelayBefore(0));
        Assert.Equal(TimeSpan.Zero, zero.DelayBefore(7));

        var negative = new PollSchedule(TimeSpan.FromSeconds(-3), TimeSpan.FromSeconds(10), 1.5);
        Assert.Equal(TimeSpan.Zero, negative.DelayBefore(0));
        Assert.Equal(TimeSpan.Zero, negative.DelayBefore(7));
    }

    [Fact]
    public void ANegativeAttemptIsAProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PollSchedule.Login.DelayBefore(-1));
    }

    /// <summary>
    /// The counting helper sums the schedule it is given — including the growth —
    /// and stops at the first delay that would overrun the budget. For the login
    /// schedule inside twenty seconds: 0.5 + 0.75 + 1.125 + 1.6875 + 2.53125 +
    /// 3×4 = 18.59, and the ninth would push past 20.
    /// </summary>
    [Fact]
    public void AttemptsWithinCountsTheGrowingWaitsItWouldActuallySpend()
    {
        Assert.Equal(9, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(1.5)));
        // A budget of zero fits nothing: the first wait alone is over it.
        Assert.Equal(0, PollSchedule.Login.AttemptsWithin(TimeSpan.Zero));

        // Activation starts at five seconds, so the same twenty-second budget fits
        // fewer attempts even though the ceiling is the same.
        Assert.Equal(2, PollSchedule.Activation.AttemptsWithin(TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    /// The two shipped schedules are separate values, not one schedule read twice:
    /// a change to the login cadence must not silently move the activation cadence.
    /// </summary>
    [Fact]
    public void TheTwoShippedSchedulesAreDistinctValues()
    {
        Assert.NotEqual(PollSchedule.Login, PollSchedule.Activation);
        Assert.NotSame(PollSchedule.Login, PollSchedule.Activation);
    }
}
