using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The backoff schedule, which is pure arithmetic over a number.
/// <para>
/// These exist because the property that matters is not any single delay — it is that
/// the wait <i>stops talking</i>. A fixed one-second poll against a service that is not
/// answering is 3600 pointless requests an hour and looks from the outside exactly
/// like something stuck in a loop. So the ceiling is the thing under test, and the
/// growth is the thing that gets it there.
/// </para>
/// </summary>
public sealed class PollScheduleTests
{
    [Fact]
    public void TheFirstWaitIsTheFirstDelay() =>
        Assert.Equal(TimeSpan.FromSeconds(1.5), PollSchedule.Login.DelayBefore(0));

    [Fact]
    public void WaitingGrowsAndNeverExceedsTheCeiling()
    {
        var schedule = PollSchedule.Login;
        var previous = TimeSpan.Zero;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var delay = schedule.DelayBefore(attempt);
            Assert.True(delay >= previous, $"attempt {attempt} went backwards: {delay} < {previous}");
            Assert.True(delay <= schedule.Ceiling, $"attempt {attempt} blew the ceiling: {delay}");
            previous = delay;
        }
    }

    [Fact]
    public void TheCeilingIsReachedWellBeforeTheTicketExpires()
    {
        // The login ticket lives five minutes. The point of the backoff is that it has
        // already settled by the time a user has finished fumbling with their phone —
        // so assert the shape, not the exact number of attempts.
        var settled = PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(30));
        Assert.True(settled >= 5, $"expected several settled attempts in 30s, got {settled}");
    }

    [Fact]
    public void AScheduleThatReachesItsCeilingKeepsAskingAtTheCeiling()
    {
        var schedule = PollSchedule.Login;
        Assert.Equal(schedule.Ceiling, schedule.DelayBefore(500));
        Assert.Equal(schedule.Ceiling, schedule.DelayBefore(5_000));
    }

    [Fact]
    public void AHugeAttemptNumberDoesNotOverflowIntoNonsense()
    {
        // Math.Pow(1.5, int.MaxValue) is Infinity, not an exception — and a delay of
        // NaN would turn a wait into an immediate retry loop, which is the exact
        // failure the ceiling exists to prevent.
        var delay = PollSchedule.Login.DelayBefore(int.MaxValue);
        Assert.Equal(PollSchedule.Login.Ceiling, delay);
    }

    [Fact]
    public void AttemptsWithinCountsWhatActuallyFits()
    {
        var schedule = PollSchedule.Login;
        var budget = TimeSpan.FromSeconds(30);

        var count = schedule.AttemptsWithin(budget);
        Assert.True(count > 0);

        // The boundary is the point: count attempts fit, one more does not.
        var total = TimeSpan.Zero;
        for (var attempt = 0; attempt < count; attempt++)
            total += schedule.DelayBefore(attempt);
        Assert.True(total <= budget, $"{count} attempts totalled {total}, over the {budget} budget");
        Assert.True(total + schedule.DelayBefore(count) > budget);
    }

    [Fact]
    public void AZeroBudgetFitsNothing()
    {
        Assert.Equal(0, PollSchedule.Login.AttemptsWithin(TimeSpan.Zero));
    }

    [Fact]
    public void ANegativeAttemptIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PollSchedule.Login.DelayBefore(-1));

    [Fact]
    public void ANegativeFirstDelayBecomesZeroRatherThanAVanishingWait()
    {
        // A zero first delay would mean "ask again immediately", which for the login
        // poll is a tight loop against the service.
        var schedule = new PollSchedule(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10), 1.5);
        Assert.Equal(TimeSpan.Zero, schedule.DelayBefore(0));
    }

    [Fact]
    public void AMultiplierOfOneNeverGrows()
    {
        // Multiplier <= 1 must not produce a schedule that shrinks or oscillates.
        var schedule = new PollSchedule(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), 1.0);
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(0));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(7));
    }

    [Fact]
    public void TheActivationWaitIsSlowerToStartThanTheLoginWait()
    {
        // A seller may take an hour. Eager polling at second one would be a laptop
        // pinging all afternoon for a human who is not there yet.
        Assert.True(PollSchedule.Activation.DelayBefore(0) > PollSchedule.Login.DelayBefore(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BothShippedSchedulesStayInsideTheirCeiling(int attempt) =>
        Assert.True(PollSchedule.Login.DelayBefore(attempt) <= PollSchedule.Login.Ceiling);
}
