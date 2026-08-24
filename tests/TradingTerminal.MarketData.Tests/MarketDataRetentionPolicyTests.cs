using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.MarketData.Tests;

/// <summary>
/// What retention deletes, and — more importantly — what it refuses to.
///
/// <para>Every case here is a boundary condition on a destructive operation, which is why the policy
/// is a pure function: getting one of these backwards deletes a user's data, and none of them need a
/// database to check.</para>
/// </summary>
public sealed class MarketDataRetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EachStreamIsCutAtItsOwnWindow()
    {
        var plan = MarketDataRetentionPolicy.Plan(
            Options(quotes: 30, trades: 10, bars: 0, depthDays: 2),
            Now,
            earliestStoredUtc: Now.AddYears(-1),
            oldestPendingArchiveUtc: null);

        // Bars are absent because 0 means keep forever, not delete everything.
        Assert.Equal(
            [MarketDataStream.Quotes, MarketDataStream.Trades, MarketDataStream.Depth],
            plan.Select(cut => cut.Stream));
        Assert.Equal(Now.AddDays(-30), plan[0].ToUtc);
        Assert.Equal(Now.AddDays(-10), plan[1].ToUtc);
        Assert.Equal(Now.AddDays(-2), plan[2].ToUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ZeroOrNegativeDaysMeansKeepForever(int days)
    {
        // The dangerous misreading. `0` in a settings box must never be "cut off at now" — that would
        // wipe the stream the first time somebody cleared the field.
        var plan = MarketDataRetentionPolicy.Plan(
            Options(quotes: days, trades: days, bars: days, depthDays: days),
            Now,
            earliestStoredUtc: Now.AddYears(-5),
            oldestPendingArchiveUtc: null);

        Assert.Empty(plan);
    }

    [Fact]
    public void NothingIsPlannedWhenTheSweepIsOff()
    {
        var options = Options(quotes: 1, trades: 1, bars: 1, depthDays: 1);
        options.RetentionSweepEnabled = false;

        Assert.Empty(MarketDataRetentionPolicy.Plan(options, Now, Now.AddYears(-1), null));
    }

    [Fact]
    public void NothingIsPlannedWhenTheStoreIsEmpty()
    {
        Assert.Empty(MarketDataRetentionPolicy.Plan(
            Options(quotes: 1, trades: 1, bars: 1, depthDays: 1),
            Now,
            earliestStoredUtc: null,
            oldestPendingArchiveUtc: null));
    }

    [Fact]
    public void NothingIsPlannedWhenTheOldestDataIsAlreadyInsideEveryWindow()
    {
        // The steady state once retention has caught up: no delete is issued at all, so a sweep on a
        // healthy store costs one extent query rather than four table scans.
        var plan = MarketDataRetentionPolicy.Plan(
            Options(quotes: 30, trades: 30, bars: 0, depthDays: 14),
            Now,
            earliestStoredUtc: Now.AddHours(-1),
            oldestPendingArchiveUtc: null);

        Assert.Empty(plan);
    }

    [Fact]
    public void ACutStartsAtTheEpochRatherThanAtDateTimeMinValue()
    {
        // The stores convert this bound to epoch microseconds. DateTime.MinValue would go negative.
        var cut = Assert.Single(MarketDataRetentionPolicy.Plan(
            Options(quotes: 5, trades: 0, bars: 0, depthDays: 0),
            Now,
            Now.AddYears(-1),
            null));

        Assert.Equal(DateTime.UnixEpoch, cut.FromUtc);
        Assert.Equal(DateTime.UnixEpoch, MarketDataRetentionPolicy.Floor);
    }

    [Fact]
    public void DepthIsCutInHoursRatherThanDays()
    {
        // The whole reason depth has its own unit. One row per book level per snapshot outgrows every
        // other stream by orders of magnitude, and the only thing that reads it back — the order
        // book's warm start — replays thirty minutes. A day-granular window kept hundreds of times
        // what anything asked for.
        var options = new MarketDataStoreOptions
        {
            QuoteRetentionDays = 0,
            TradeRetentionDays = 0,
            BarRetentionDays = 0,
            DepthRetentionHours = 1,
        };

        var cut = Assert.Single(MarketDataRetentionPolicy.Plan(options, Now, Now.AddYears(-1), null));

        Assert.Equal(MarketDataStream.Depth, cut.Stream);
        Assert.Equal(Now.AddHours(-1), cut.ToUtc);
    }

    [Fact]
    public void TheShippedDepthWindowIsAnHour()
    {
        Assert.Equal(1, new MarketDataStoreOptions().DepthRetentionHours);
    }

    // ── The archive interaction: the part that can destroy data ──────────────────────────────────

    [Fact]
    public void RetentionWillNotDeletePastWhatTheArchiveStillOwes()
    {
        // The whole reason this clamp exists. A 7-day window with a monthly archive period would
        // otherwise delete each month before the archiver ever bundled it — silently destroying data
        // the user believed was being backed up.
        var pending = Now.AddDays(-20);

        var cut = Assert.Single(MarketDataRetentionPolicy.Plan(
            Options(quotes: 7, trades: 0, bars: 0, depthDays: 0),
            Now,
            earliestStoredUtc: Now.AddYears(-1),
            oldestPendingArchiveUtc: pending));

        Assert.Equal(pending, cut.ToUtc);
        Assert.True(cut.ClampedByPendingArchive);
    }

    [Fact]
    public void TheClampNeverEXTENDSTheWindow()
    {
        // A pending window NEWER than the cutoff must leave the cutoff alone. Moving it forward would
        // make the archive's backlog delete data the retention window said to keep.
        var cut = Assert.Single(MarketDataRetentionPolicy.Plan(
            Options(quotes: 30, trades: 0, bars: 0, depthDays: 0),
            Now,
            earliestStoredUtc: Now.AddYears(-1),
            oldestPendingArchiveUtc: Now.AddDays(-2)));

        Assert.Equal(Now.AddDays(-30), cut.ToUtc);
        Assert.False(cut.ClampedByPendingArchive);
    }

    [Fact]
    public void ClampingCanCancelACutEntirely()
    {
        // Pending back to the beginning of time means there is nothing safe to delete at all.
        Assert.Empty(MarketDataRetentionPolicy.Plan(
            Options(quotes: 7, trades: 7, bars: 0, depthDays: 7),
            Now,
            earliestStoredUtc: Now.AddYears(-1),
            oldestPendingArchiveUtc: DateTime.UnixEpoch));
    }

    [Fact]
    public void TheClampIsSkippedWhenTheUserTurnedItOff()
    {
        var options = Options(quotes: 7, trades: 0, bars: 0, depthDays: 0);
        options.RespectPendingArchives = false;

        var cut = Assert.Single(MarketDataRetentionPolicy.Plan(
            options, Now, Now.AddYears(-1), oldestPendingArchiveUtc: Now.AddDays(-20)));

        Assert.Equal(Now.AddDays(-7), cut.ToUtc);
        Assert.False(cut.ClampedByPendingArchive);
    }

    [Fact]
    public void ArchivingThatNeverSucceededDoesNotHoldRetentionHostage()
    {
        // The trap this guards: enable archiving, never finish the Telegram login, and every window is
        // pending forever — so a clamp that trusted the backlog would stop retention permanently and
        // reintroduce the unbounded growth the whole feature exists to stop.
        Assert.Null(MarketDataRetentionPolicy.ArchiveFloor(
            archivingEnabled: true,
            hasSucceededBefore: false,
            pendingWindowStarts: [Now.AddDays(-90), Now.AddDays(-60)]));
    }

    [Fact]
    public void ArchivingThatIsOffDoesNotClampEither()
    {
        Assert.Null(MarketDataRetentionPolicy.ArchiveFloor(
            archivingEnabled: false,
            hasSucceededBefore: true,
            pendingWindowStarts: [Now.AddDays(-90)]));
    }

    [Fact]
    public void TheFloorIsTheOLDESTPendingWindow()
    {
        // Not the newest, and not the first in the list — the coverage view is returned newest-first.
        Assert.Equal(
            Now.AddDays(-90),
            MarketDataRetentionPolicy.ArchiveFloor(
                archivingEnabled: true,
                hasSucceededBefore: true,
                pendingWindowStarts: [Now.AddDays(-10), Now.AddDays(-90), Now.AddDays(-40)]));
    }

    [Fact]
    public void NoPendingWindowsMeansNoFloor()
    {
        Assert.Null(MarketDataRetentionPolicy.ArchiveFloor(
            archivingEnabled: true,
            hasSucceededBefore: true,
            pendingWindowStarts: []));
    }

    [Fact]
    public void TheShippedDefaultsDeleteSomethingButNeverBars()
    {
        // Guards the defaults themselves: a shipped install must actually prune, and must not start
        // deleting the bar cache — the most valuable and smallest stream — without someone asking.
        var plan = MarketDataRetentionPolicy.Plan(
            new MarketDataStoreOptions(),
            Now,
            earliestStoredUtc: Now.AddYears(-1),
            oldestPendingArchiveUtc: null);

        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, cut => cut.Stream == MarketDataStream.Bars);
    }

    /// <summary>Depth is in HOURS; every other stream is in days. Callers pass whole days of depth so
    /// the existing cases keep meaning what they say.</summary>
    private static MarketDataStoreOptions Options(int quotes, int trades, int bars, int depthDays) => new()
    {
        QuoteRetentionDays = quotes,
        TradeRetentionDays = trades,
        BarRetentionDays = bars,
        DepthRetentionHours = depthDays * 24,
    };
}
