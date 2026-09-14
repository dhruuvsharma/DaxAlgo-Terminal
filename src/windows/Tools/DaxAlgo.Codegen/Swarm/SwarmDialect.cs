using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>
/// What the swarm says, and reads, for one kind of unit.
///
/// <para>The run itself — plan, fan out, gate, route repairs to owners, keep the best version, stop on a
/// stall — does not depend on what a unit is written against. What does is small and all here: the role
/// instructions, what each builder is handed beside its task, which parts of a reply are files, and what
/// the critics are shown. The widget SDK and the Blocks SDK each supply one, so the loop that four
/// months of measured failures shaped is not written twice.</para>
/// </summary>
public interface ISwarmDialect
{
    /// <summary>The planner's role instruction.</summary>
    string Planner(AuthoringKind kind, int maxTasks);

    /// <summary>A builder's role instruction.</summary>
    string Builder(BuildTask task, UnitContract contract);

    /// <summary>A repair's role instruction.</summary>
    string Fixer(BuildTask task, UnitContract contract);

    /// <summary>The message a builder is sent: its task, its dependencies, whatever else it needs.</summary>
    string ComposeBuild(SwarmContext context, BuildTask task, BuildPlan plan);

    /// <summary>The message a repair is sent.</summary>
    string ComposeRepair(
        SwarmContext context, BuildTask task, IReadOnlyList<VerificationFinding> findings, BuildPlan plan);

    /// <summary>The files in a builder's reply.</summary>
    IReadOnlyList<StrategyFile> FilesIn(StrategyCodegenResponse response);

    /// <summary>What the critics judge, or null when there is nothing they can be shown.</summary>
    Task<GauntletSubject?> SubjectAsync(
        GateResult verdict,
        IReadOnlyList<StrategyFile> files,
        AuthoringKind kind,
        IUnitRasterizer? rasterizer,
        CancellationToken ct);
}

/// <summary>The widget SDK's dialect: <c>IStrategyKernel</c> / <c>IVisualizer</c>, panels and widgets.</summary>
public sealed class SdkSwarmDialect : ISwarmDialect
{
    public static SdkSwarmDialect Instance { get; } = new();

    private SdkSwarmDialect()
    {
    }

    public string Planner(AuthoringKind kind, int maxTasks) => SwarmPrompts.Planner(kind, maxTasks);

    public string Builder(BuildTask task, UnitContract contract) => SwarmPrompts.Builder(task, contract);

    public string Fixer(BuildTask task, UnitContract contract) => SwarmPrompts.Fixer(task, contract);

    public string ComposeBuild(SwarmContext context, BuildTask task, BuildPlan plan) =>
        context.ComposeBuild(task, plan);

    public string ComposeRepair(
        SwarmContext context, BuildTask task, IReadOnlyList<VerificationFinding> findings, BuildPlan plan) =>
        context.ComposeRepair(task, findings, plan);

    // Prose in a fence is not code whoever wrote it. The single-conversation path has refused it since
    // the day it cost three generations — the fix loop reads CS1003 and tries to FIX THE PROSE — and a
    // second path that compiled it happily is exactly the defect this area keeps producing.
    public IReadOnlyList<StrategyFile> FilesIn(StrategyCodegenResponse response) =>
        [.. response.FileList.Where(f => CodegenCodeExtractor.LooksLikeCode(f.Content))];

    public async Task<GauntletSubject?> SubjectAsync(
        GateResult verdict,
        IReadOnlyList<StrategyFile> files,
        AuthoringKind kind,
        IUnitRasterizer? rasterizer,
        CancellationToken ct) =>
        verdict.Unit is null
            ? null
            : await GauntletSubjects.BuildAsync(
                AuthoredUnitPreview.Create(verdict.Unit), files, verdict.Report, rasterizer, ct).ConfigureAwait(false);
}
