using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The session rail: what a row says, how the rows are grouped, and what the search box does.
///
/// <para><b>The rail was a flat, unsearchable list of rows that mostly read "My custom strategy".</b>
/// A session is only named from its brief AFTER a turn has run, so every un-run conversation carries
/// the default identity — a column of identical rows for distinct pieces of work. Sessions accumulate
/// by design (that is what saving them is for), so the failure compounds: at thirty entries the
/// history built to make the builder usable across sittings is the thing making it unusable.</para>
///
/// <para>These pin the three properties that fix it — the opening brief on the row, a recency bucket
/// to scroll by, and a filter — at the level they are computed, because the view cannot be asserted on
/// from here and a rail that silently stops filtering looks exactly like a rail with one session.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class SessionHistoryTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-history-" + Guid.NewGuid().ToString("N"));

    public SessionHistoryTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_row_carries_the_brief_the_session_opened_with()
    {
        var session = Snapshot("scalper", "My custom strategy", DateTime.UtcNow,
            User("Fade liquidity sweeps at the prior day's low.\nExit at VWAP."),
            Assistant("Here is your strategy."));

        // Flattened to one line: a rail row is one line tall, so a brief with a newline in it would
        // render as its first fragment and silently drop the constraint on the next line.
        Assert.Equal("Fade liquidity sweeps at the prior day's low. Exit at VWAP.", session.Summary);
    }

    [Fact]
    public void A_long_brief_is_trimmed_rather_than_allowed_to_set_the_rail_width()
    {
        var session = Snapshot("verbose", "Verbose", DateTime.UtcNow, User(new string('x', 400)));

        Assert.True(session.Summary.Length <= 110, $"summary was {session.Summary.Length} chars");
        // A trim the reader cannot see is a trim that reads as data loss.
        Assert.EndsWith("…", session.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_with_nothing_typed_in_it_has_no_brief_to_show()
    {
        // Not an empty-string oversight: the row's second line goes through a string-to-visibility
        // converter, so "" is what collapses it rather than reserving a blank line.
        Assert.Equal(string.Empty, Snapshot("fresh", "My custom strategy", DateTime.UtcNow).Summary);
    }

    [Fact]
    public void Only_the_users_own_turns_are_counted()
    {
        // One brief that produced nine tool rows is one turn of work, not ten — a count that inflated
        // with the assistant's replies would say more about how chatty the model is than about the work.
        var session = Snapshot("counted", "Counted", DateTime.UtcNow,
            User("first"), Assistant("a"), Assistant("b"), User("second"), Assistant("c"));

        Assert.Equal(2, session.TurnCount);
        Assert.Equal("2 turns", session.TurnLabel);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-1, "Yesterday")]
    [InlineData(-3, "Previous 7 days")]
    [InlineData(-30, "Older")]
    public void Rows_fall_into_the_recency_bucket_the_rail_groups_by(int daysAgo, string expected)
    {
        // Built from LOCAL midday and converted back, because the bucket has to be the day the person
        // who saved it would name: a session saved at 23:50 must read "Today" to them whatever UTC says.
        var localDay = DateTime.Now.Date.AddDays(daysAgo).AddHours(12);

        Assert.Equal(expected, Snapshot("dated", "Dated", localDay.ToUniversalTime()).Group);
    }

    [Fact]
    public void An_empty_search_hides_nothing()
    {
        var session = Snapshot("keep", "Keep me", DateTime.UtcNow, User("anything"));

        Assert.True(session.Matches(null));
        Assert.True(session.Matches(string.Empty));
        Assert.True(session.Matches("   "), "a box holding only spaces is an untouched box");
    }

    [Fact]
    public void Search_reaches_the_name_the_id_and_the_brief()
    {
        var session = Snapshot("delta-div", "Cumulative delta divergence", DateTime.UtcNow,
            User("Fade the move when price prints a new session low."));

        Assert.True(session.Matches("divergence"), "the display name");
        Assert.True(session.Matches("DELTA-DIV"), "the id, case-insensitively");
        Assert.True(session.Matches("session low"), "the brief — which is how people remember a chat");
        Assert.False(session.Matches("kalman"));
    }

    [Fact]
    public void The_rail_shows_the_sessions_that_match_and_says_so_when_none_do()
    {
        AuthoringSessionStore.Save(Snapshot("sweep", "Liquidity sweep", DateTime.UtcNow, User("sweeps")));
        AuthoringSessionStore.Save(Snapshot("momo", "Momentum breakout", DateTime.UtcNow, User("breakouts")));

        using var vm = BuildViewModel();
        Assert.True(vm.HasSavedSessions);
        Assert.Equal(2, vm.VisibleSessions.Count);

        vm.SessionQuery = "momentum";
        Assert.Equal("momo", Assert.Single(vm.VisibleSessions).StrategyId);
        Assert.False(vm.HasNoSessionMatches);

        vm.SessionQuery = "kalman";
        Assert.Empty(vm.VisibleSessions);
        // "nothing matched" and "you have no sessions" need different words on screen.
        Assert.True(vm.HasNoSessionMatches);

        vm.ClearSessionQueryCommand.Execute(null);
        Assert.Equal(2, vm.VisibleSessions.Count);
    }

    [Fact]
    public void Narrowing_the_search_past_the_open_session_does_not_load_a_different_one()
    {
        // The rail's ListBox drives SelectedSavedSession, and setting that RESTORES a conversation.
        // Refiltering repopulates the list, so without the restore guard, typing a search that excludes
        // whatever is open would swap the user's working session out from under them mid-sentence.
        AuthoringSessionStore.Save(Snapshot("sweep", "Liquidity sweep", DateTime.UtcNow, User("sweeps")));
        AuthoringSessionStore.Save(Snapshot("momo", "Momentum breakout", DateTime.UtcNow, User("breakouts")));

        using var vm = BuildViewModel();
        var openId = vm.StrategyId;

        vm.SessionQuery = "nothing matches this";

        Assert.Equal(openId, vm.StrategyId);
        Assert.Empty(vm.VisibleSessions);
    }

    private static StrategyAuthoringViewModel BuildViewModel() => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance);

    private static AuthoringChatEntry User(string text) =>
        new(AuthoringChatEntry.User, text, DateTime.Now);

    private static AuthoringChatEntry Assistant(string text) =>
        new(AuthoringChatEntry.Assistant, text, DateTime.Now);

    private static AuthoringSessionSnapshot Snapshot(
        string id, string name, DateTime updatedUtc, params AuthoringChatEntry[] chat) =>
        new(id, name, chat, [], [new StrategyFile(StrategyFile.DefaultName, "// nothing")],
            UpdatedUtc: updatedUtc);

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];

        public event EventHandler? Changed;

        public StrategyCatalogEntry? Find(string id) => null;

        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public bool Remove(string id) => false;
    }
}
