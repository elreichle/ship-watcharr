using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// The stopping rules. These exist because every failure mode here is one where the scraper keeps
/// making requests it should not: a pagination bug walking forever, or a run grinding through its
/// whole budget against an archive that is plainly down.
/// </summary>
public class ScrapeBudgetTests
{
    private static ScrapeBudget Budget(
        int maxRequests = 10,
        int maxConsecutiveFailures = 3,
        TimeSpan? maxDuration = null,
        TimeProvider? time = null) =>
        new(maxRequests, maxConsecutiveFailures, maxDuration ?? TimeSpan.FromHours(2), time);

    [Fact]
    public void Allows_requests_until_the_cap_is_reached()
    {
        var budget = Budget(maxRequests: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(budget.CanContinue(out _));
            budget.RecordSuccess();
        }

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Cap, reason);
        Assert.True(budget.HitRequestCap);
        Assert.Equal(3, budget.RequestsMade);
    }

    [Fact]
    public void Failed_requests_count_against_the_cap()
    {
        // A failed request still costs AO3 real work, so it must not be free.
        var budget = Budget(maxRequests: 2, maxConsecutiveFailures: 99);

        budget.RecordFailure();
        budget.RecordFailure();

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Cap, reason);
    }

    [Fact]
    public void Breaker_opens_after_consecutive_failures()
    {
        var budget = Budget(maxConsecutiveFailures: 3);

        budget.RecordFailure();
        budget.RecordFailure();
        Assert.True(budget.CanContinue(out _));

        budget.RecordFailure();

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Breaker, reason);
        Assert.True(budget.BreakerOpen);
    }

    [Fact]
    public void Success_resets_the_failure_streak()
    {
        // The threshold counts *consecutive* failures, so an archive that is flaky but functional
        // must not trip it. Without the reset, any 3 failures in a long run would abort it.
        var budget = Budget(maxRequests: 100, maxConsecutiveFailures: 3);

        budget.RecordFailure();
        budget.RecordFailure();
        budget.RecordSuccess();
        budget.RecordFailure();
        budget.RecordFailure();

        Assert.True(budget.CanContinue(out _));
        Assert.Equal(2, budget.ConsecutiveFailures);
        Assert.False(budget.BreakerOpen);
    }

    [Fact]
    public void Breaker_stays_open_once_tripped()
    {
        var budget = Budget(maxConsecutiveFailures: 1);
        budget.RecordFailure();

        budget.RecordSuccess();

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Breaker, reason);
    }

    [Fact]
    public void Breaker_takes_precedence_over_the_request_cap()
    {
        // Both conditions true at once. The breaker is the more urgent diagnosis — "AO3 is failing"
        // is what the operator needs to see, not "this run used its allowance".
        var budget = Budget(maxRequests: 2, maxConsecutiveFailures: 2);

        budget.RecordFailure();
        budget.RecordFailure();

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Breaker, reason);
    }

    [Fact]
    public void Cache_hits_move_nothing()
    {
        var budget = Budget(maxRequests: 1);

        budget.RecordCacheHit();
        budget.RecordCacheHit();

        Assert.True(budget.CanContinue(out _));
        Assert.Equal(0, budget.RequestsMade);
    }

    [Fact]
    public void Stops_at_the_wall_clock_cap()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var budget = Budget(maxRequests: 1000, maxDuration: TimeSpan.FromHours(2), time: time);

        Assert.True(budget.CanContinue(out _));

        time.Advance(TimeSpan.FromHours(2));

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.TimeCap, reason);
        Assert.True(budget.HitTimeCap);
    }

    [Fact]
    public void Budget_built_from_options_uses_configured_limits()
    {
        var options = new Ao3HttpClientOptions { MaxRequestsPerRun = 4, MaxConsecutiveFailures = 2 };
        var budget = new ScrapeBudget(options);

        for (var i = 0; i < 4; i++) budget.RecordSuccess();

        Assert.False(budget.CanContinue(out var reason));
        Assert.Equal(ScrapeStopReason.Cap, reason);
    }

    [Fact]
    public void Default_options_match_the_documented_politeness_limits()
    {
        var options = new Ao3HttpClientOptions();

        Assert.Equal(500, options.MaxRequestsPerRun);
        Assert.Equal(3, options.MaxConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(5), options.MinDelayBetweenRequests);
        Assert.Equal(TimeSpan.FromSeconds(8), options.MaxDelayBetweenRequests);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
