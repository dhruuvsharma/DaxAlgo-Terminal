using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

/// <summary>
/// Which half of the gauntlet a critic belongs to.
///
/// <para>Two panels because a trading unit fails in two unrelated ways, and one critic asked to watch
/// for both watches for neither. A panel that looks superb and repaints is worse than one that looks
/// plain and does not.</para>
/// </summary>
public enum CriticPanel
{
    /// <summary>The picture: what the user sees. Judged against the reference bar.</summary>
    Picture,

    /// <summary>The maths and the market behaviour: what the unit believes. Judged against the domain.</summary>
    Quant,
}

/// <summary>What one critic concluded.</summary>
/// <param name="CriticId">Who said it.</param>
/// <param name="Panel">Which half.</param>
/// <param name="Findings">In the SAME shape the ladder produces, so the repair path is the one that
/// already exists. A second diagnostics vocabulary would need a second router and a second fixer.</param>
/// <param name="Verdict">One line for the transcript.</param>
/// <param name="Ran">False when the critic could not be run at all — no provider, no picture — which is
/// distinct from running and finding nothing. A skipped critic must never read as a pass.</param>
public sealed record CriticVerdict(
    string CriticId,
    CriticPanel Panel,
    IReadOnlyList<VerificationFinding> Findings,
    string Verdict,
    bool Ran = true)
{
    public bool Passes => Ran && Findings.Count == 0;

    public static CriticVerdict Skipped(string id, CriticPanel panel, string why) =>
        new(id, panel, [], why, Ran: false);
}

/// <summary>
/// One judgement over a finished unit.
///
/// <para>An interface rather than a sealed set, because the useful critics are not known in advance —
/// the ones here are the failures this codebase has actually seen, and the next one will come from a
/// user's screenshot.</para>
/// </summary>
public interface IUnitCritic
{
    /// <summary>Stable, greppable, and the prefix of every finding code it emits.</summary>
    string Id { get; }

    CriticPanel Panel { get; }

    /// <summary>True when this critic needs to SEE the render rather than read its commands.</summary>
    bool NeedsPicture { get; }

    Task<CriticVerdict> JudgeAsync(GauntletSubject subject, ReferenceBar bar, CancellationToken ct = default);
}

/// <summary>
/// The six critics, as definitions rather than as six classes.
///
/// <para>They differ only in what they are told to look for, so they are a table of prompts driven by
/// one <see cref="ModelCritic"/>. Six near-identical classes would put the interesting part — the
/// domain knowledge — in six places and invite them to drift; here the whole panel can be read at
/// once, which is how anybody would want to review it.</para>
/// </summary>
public static class Critics
{
    /// <summary>
    /// Compares the render against the reference, and is the only critic that must SEE it.
    ///
    /// <para>Blind comparison where a reference exists: which is better, and what is the largest gap.
    /// Asking for the largest gap rather than a list is deliberate — a critic that returns fifteen
    /// small notes produces a repair turn that changes fifteen things and improves none of them.</para>
    /// </summary>
    public const string Picture = "picture";

    /// <summary>Axes, scales, legends, label collisions, density, and the colour conventions a trading
    /// panel is read by. The failures here are invisible to every rung: a chart can emit thousands of
    /// correct primitives and still have no y-axis.</summary>
    public const string ChartCraft = "chartcraft";

    /// <summary>Look-ahead, repainting, warm-up, session and timezone handling, tick size, unfloored
    /// denominators. <b>Verification cannot catch a wrong indicator</b> — an RSI smoothed with an EMA
    /// instead of Wilder's compiles, runs, draws and trades; it is simply not an RSI.</summary>
    public const string MarketLogic = "marketlogic";

    /// <summary>Whether the unit lives inside the data it declared, keeps its buffers bounded, and
    /// never reads the wall clock. Each of these is a defect that only appears hours into a live
    /// session, which is exactly when nobody is watching for it.</summary>
    public const string DataContract = "datacontract";

    /// <summary>What a strategy does to its virtual book: sizing, exits, exposure, order semantics.</summary>
    public const string Book = "book";

    /// <summary>The window as ONE thing — panels coherent with each other, parameters actually wired,
    /// nothing declared and then ignored. The failures here are between the parts, so they are the
    /// failures a per-file builder is structurally unable to see.</summary>
    public const string Integration = "integration";
}
