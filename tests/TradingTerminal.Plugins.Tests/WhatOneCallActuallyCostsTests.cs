using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// How big the prompt is that every single call in a run carries.
///
/// <para><b>Measured from a live run and it is the dominant cost of the whole pipeline.</b> One build
/// of one visualizer billed 738,546 input tokens against 76,283 output — the model was paid ten times
/// more for what it was TOLD than for what it wrote. Sixteen calls (a planner, nine builders and
/// repairs, six critics) at roughly 46,000 input tokens each, and the shared pack is nearly all of
/// it.</para>
///
/// <para>The design says so in its own words: the pack is "the cached prefix of every request in a
/// run", which is exactly right against an API that caches prefixes and exactly wrong through an agent
/// CLI, where every call is a new process and a new session and the prefix is paid for again.</para>
///
/// <para>So the size of that prefix is a number worth holding still. This prints it, and fails if the
/// trimming that is supposed to cut it stops cutting — a silent regression there is a bill nobody sees
/// until a window disappears.</para>
/// </summary>
public sealed class WhatOneCallActuallyCostsTests(ITestOutputHelper output)
{
    /// <summary>Rough, and deliberately so: four characters to a token is close enough to turn a length
    /// into something comparable with a provider's bill, and precision here would imply a certainty the
    /// tokeniser does not give us from outside.</summary>
    private static int Tokens(int characters) => characters / 4;

    private const string FootprintBrief =
        "A volume footprint chart: bid and ask volume clustered by price inside each time bar, with "
        + "the point of control and the value area marked.";

    [Fact]
    public void The_surface_is_cut_to_the_brief()
    {
        var pack = StrategyContextPack.Load();
        var cut = SdkSurfaceSelector.For(pack.SdkSurfaceSource, FootprintBrief);

        var before = pack.SdkSurface.Length;
        var after = cut.Length;
        var saved = before - after;

        output.WriteLine($"SDK surface   {before,8:N0} chars  (~{Tokens(before),6:N0} tokens)");
        output.WriteLine($"after the cut {after,8:N0} chars  (~{Tokens(after),6:N0} tokens)");
        output.WriteLine($"saved         {saved,8:N0} chars  ({(double)saved / before:P0})");

        Assert.True(
            saved > 0,
            "the selector exists to cut the surface to the brief; cutting nothing means every call "
            + "carries every type in the SDK, and the surface is the largest thing in the prompt");
    }

    [Fact]
    public void The_whole_prompt_one_call_carries_is_reported()
    {
        // Not asserted to a number — that would fail on every legitimate edit to the pack. Printed,
        // because the figure is the one nobody looks at and the one that decides what a run costs.
        var pack = StrategyContextPack.Load();
        var skills = StrategySkillLibrary.Load();

        var chosen = skills.SelectFor(FootprintBrief);
        var cut = SdkSurfaceSelector.For(pack.SdkSurfaceSource, FootprintBrief);
        var composed = StrategySkillLibrary.Compose(
            StrategyContextPack.Join(cut, pack.Conventions), chosen);

        output.WriteLine($"conventions   {pack.Conventions.Length,8:N0} chars");
        output.WriteLine($"surface (cut) {cut.Length,8:N0} chars");

        foreach (var skill in chosen)
            output.WriteLine($"skill {skill.Id,-20} {skill.Body.Length,8:N0} chars");

        output.WriteLine($"TOTAL         {composed.Length,8:N0} chars  (~{Tokens(composed.Length),6:N0} tokens)");
        output.WriteLine(string.Empty);
        output.WriteLine($"A sixteen-call run therefore carries ~{Tokens(composed.Length) * 16,9:N0} input tokens");
        output.WriteLine("before a single line of code is written.");

        Assert.True(composed.Length > 0);
    }
}
