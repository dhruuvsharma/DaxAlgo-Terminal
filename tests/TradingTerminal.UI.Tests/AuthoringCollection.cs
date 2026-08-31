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
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthoringCollection
{
    public const string Name = "Authoring pane";
}
