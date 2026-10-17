using Microsoft.Extensions.Time.Testing;
using Quorum.Modules.Agents.Chat;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The total time of an answer does not run while the answer waits for a person (spec 004, D-8).</summary>
public sealed class AnswerDeadlineTests
{
    private static readonly TimeSpan Total = TimeSpan.FromSeconds(10);

    [Fact]
    public void TheTokenIsCancelledWhenTheTotalTimeRunsOut()
    {
        var clock = new FakeTimeProvider();
        var deadline = new AnswerDeadline(Total, clock);
        using var watch = deadline.CreateWatch(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(watch.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watch.Token.IsCancellationRequested);
        Assert.True(watch.TimedOut);
        Assert.True(deadline.IsExpired);
    }

    [Fact]
    public void TimeSpentPausedDoesNotCount()
    {
        var clock = new FakeTimeProvider();
        var deadline = new AnswerDeadline(Total, clock);
        using var watch = deadline.CreateWatch(CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(6));
        using (deadline.Pause())
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            Assert.False(watch.Token.IsCancellationRequested);
            Assert.False(deadline.IsExpired);
            Assert.Equal(TimeSpan.FromSeconds(4), deadline.Remaining);
        }

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(watch.Token.IsCancellationRequested);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(watch.TimedOut);
    }

    [Fact]
    public void PausesNestAndTheClockRunsAgainWhenTheLastOneEnds()
    {
        var clock = new FakeTimeProvider();
        var deadline = new AnswerDeadline(Total, clock);
        using var watch = deadline.CreateWatch(CancellationToken.None);

        var outer = deadline.Pause();
        var inner = deadline.Pause();
        inner.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Total, deadline.Remaining);

        outer.Dispose();
        clock.Advance(Total);
        Assert.True(watch.TimedOut);
    }

    [Fact]
    public void TheOuterTokenStillCancelsAndIsNotReportedAsATimeout()
    {
        var clock = new FakeTimeProvider();
        var deadline = new AnswerDeadline(Total, clock);
        using var user = new CancellationTokenSource();
        using var watch = deadline.CreateWatch(user.Token);

        user.Cancel();

        Assert.True(watch.Token.IsCancellationRequested);
        Assert.False(watch.TimedOut);
    }

    [Fact]
    public void WithoutALimitNothingIsEverCancelled()
    {
        var clock = new FakeTimeProvider();
        var deadline = new AnswerDeadline(Timeout.InfiniteTimeSpan, clock);
        using var watch = deadline.CreateWatch(CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(1));

        Assert.False(watch.Token.IsCancellationRequested);
        Assert.False(deadline.IsExpired);
        Assert.Equal(Timeout.InfiniteTimeSpan, deadline.Remaining);
    }
}
