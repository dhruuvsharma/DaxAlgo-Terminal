using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Serialises every fixture that drives the authoring pane.
///
/// <para>xUnit runs test CLASSES in parallel, and four of them redirect the same static —
/// <c>AuthoringSessionStore.Directory</c> — in their constructor and put it back in <c>Dispose</c>.
/// Run together, one class's teardown restores the real user directory while another is still mid
/// turn, and that turn saves its fixture into the chat list of whoever ran the suite. That is the
/// exact harm the redirect exists to prevent, and the redirect cannot prevent it alone.</para>
///
/// <para>It surfaced as something less obvious: a promotion run failed
/// <c>AgentSharedContextTests.The_brief_picks_the_packs_the_agents_get</c> once and then passed four
/// times, including in isolation. An intermittent failure in a suite that gates every promotion costs
/// more than the thing it is testing, because the next real failure gets read as noise.</para>
///
/// <para><c>-m:1</c> on the solution serialises test PROJECTS, not the classes inside one, so it does
/// not cover this.</para>
///
/// <para>It covers a second shared static now: <c>UiThread.CreateRenderTimer</c>, which
/// <c>AuthoredUnitHostTests</c> swaps for a manual one and restores in <c>Dispose</c>. Any class that
/// constructs an <c>AuthoredUnitHost</c> takes a timer through that hook, so a class doing it in
/// parallel captures the manual timer meant for another test and the fire lands on the wrong host.
/// That is what happened when <c>UnitActionTests</c> was added — the same shape as the session-store
/// race, from the same cause: a new class touching state that was only ever shared by one.</para>
///
/// <para><b>Add a class here whenever it touches a mutable static</b> — the session-store directory or
/// the render-timer hook — rather than discovering the race in a promotion run.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthoringCollection
{
    public const string Name = "Authoring pane";
}
