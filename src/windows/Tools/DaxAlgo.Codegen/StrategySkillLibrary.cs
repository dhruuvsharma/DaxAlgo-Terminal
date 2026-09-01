using System.Reflection;
using System.Text;

using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>One on-demand domain pack: what it knows, and the words that mean it is relevant.</summary>
/// <param name="Id">Stable id (the file name).</param>
/// <param name="Name">Shown in the builder's activity strip when it loads.</param>
/// <param name="Triggers">Lower-cased phrases; a brief containing any of them pulls the skill in.</param>
/// <param name="Body">The markdown the model gets.</param>
public sealed record StrategySkill(
    string Id,
    string Name,
    IReadOnlyList<string> Triggers,
    string Body,
    IReadOnlyList<AuthoringKind>? Kinds = null)
{
    /// <summary>
    /// The unit kinds this pack applies to. Null or empty means both, which is right for most of them —
    /// drawing, layout and market structure read the same whichever you are writing.
    ///
    /// <para>Tagging matters where a pack teaches something one kind <b>cannot do</b>. A visualizer has
    /// no book: it cannot take a position, set a target or place an order. Loading the risk pack into a
    /// visualizer session spends context teaching stops and sizing to a unit with no way to act on
    /// them, and invites code against an API that is not there — which then fails to compile and burns
    /// a fix generation.</para>
    /// </summary>
    public IReadOnlyList<AuthoringKind> Kinds { get; init; } = Kinds ?? [];

    /// <summary>True when this pack is worth loading for <paramref name="kind"/>.</summary>
    public bool AppliesTo(AuthoringKind kind) => Kinds.Count == 0 || Kinds.Contains(kind);

    /// <summary>How well this skill matches a brief — the number of distinct triggers it hits. A
    /// count, not a boolean, so "footprint imbalance VPOC delta" outranks a passing mention of "depth".</summary>
    public int Score(string text)
    {
        var hits = 0;
        foreach (var trigger in Triggers)
        {
            if (text.Contains(trigger, StringComparison.OrdinalIgnoreCase)) hits++;
        }
        return hits;
    }
}

/// <summary>
/// The builder's domain knowledge, split out of the system prompt and loaded only when the brief calls
/// for it — order flow, quant math, risk and exits, the live window, instruments and feeds.
/// <para>
/// Why not just put it all in the pack: a monolithic prompt is paid for on every generation whether the
/// strategy is an order-flow scalper or a bar-based EMA cross, and it is shallower than a focused pack
/// because everything has to be squeezed to fit. On-demand loading makes the base prompt SMALLER and the
/// model DEEPER on the thing you actually asked for.
/// </para>
/// <para>
/// <b>Skills are chosen once per session, never per turn.</b> The system prompt is the cached prefix of
/// every request in that conversation; re-selecting skills mid-thread would change those bytes and throw
/// the prompt cache away on each turn, which costs far more than any skill saves.
/// </para>
/// </summary>
public sealed class StrategySkillLibrary
{
    internal const string ResourcePrefix = "DaxAlgo.AiContext.Skill.";

    /// <summary>Ceilings, so a brief that mentions everything doesn't rebuild the monolith we just split.</summary>
    public const int MaxSkillsPerSession = 3;

    /// <summary>
    /// Total characters of skill text one session may load — roughly 4,500 tokens, paid once and then
    /// read from cache.
    /// </summary>
    /// <remarks>
    /// <para>Raised from 12,000 when the drawing skill became the widget catalogue. At 12,000 a brief
    /// like "plot cumulative delta" loaded the order-flow pack, found 7,550 characters left, and
    /// <b>silently dropped drawing entirely</b> — so a brief explicitly asking for a picture got no
    /// drawing guidance at all. Skipping is per-skill and all-or-nothing, which makes a ceiling that
    /// nearly fits worse than one that comfortably does.</para>
    ///
    /// <para>Sized to hold the three heaviest packs together, because that is a real brief rather than a
    /// contrived one: order flow drawn as a picture with some maths behind it. The ceiling still binds —
    /// all six packs are over 26,000 — which is the point of having one.</para>
    ///
    /// <para>A pack was added in 2026-08-27 (layout), so the arithmetic moved. <c>SkillBudgetTests</c>
    /// asserts the invariant, because the failure mode is silent — a brief asking for a picture would
    /// simply arrive without the drawing catalogue, and the model would hand-roll widgets that already
    /// exist.</para>
    ///
    /// <para>It buys back more than it costs. The catalogue is the one pack that reduces <i>output</i>
    /// tokens, which are billed at several times the rate of the cached input it occupies, and a widget
    /// the model does not know about is a widget it writes from scratch and gets wrong.</para>
    ///
    /// <para><b>Raised from 18,000 on 2026-08-31, and the reason is a warning about this comment.</b>
    /// It claimed the three heaviest were "16,856 of 18,000" — a comfortable-sounding margin. They were
    /// actually <b>17,951</b>: forty-nine characters, about one sentence, from silently dropping a
    /// pack. The number in the prose had not been recomputed as the packs grew, and nothing checked it,
    /// so the ceiling read as roomy while it was effectively already reached.</para>
    ///
    /// <para>Hence <see cref="MinimumHeadroom"/>. A ceiling alone turns into a cliff nobody sees coming;
    /// asserting a margin as well makes the next pack edit fail at "this is getting tight" rather than
    /// at "a brief already lost its drawing catalogue". Cost is bounded and small — the packs are a few
    /// percent of a prompt whose bulk is the generated surface.</para>
    ///
    /// <para><b>Raised again to 21,000 on 2026-09-01, and the margin is what raised it.</b> Projection
    /// arrived — a unit can now draw in three dimensions — and the drawing pack had to teach it, which
    /// took the three heaviest to within 378 characters of the ceiling. That is the tripwire working:
    /// it fired at "this is getting tight", which is the whole reason it exists.</para>
    ///
    /// <para>The alternative was cutting the widget catalogue, and this comment already argues against
    /// that four paragraphs up: a widget the model cannot see is a widget it writes from scratch and
    /// gets wrong. Two paragraphs of the drawing pack WERE cut first — a worked snippet that restated
    /// the table above it — which bought about 500 of the 1,000. The rest is the ceiling moving.</para>
    ///
    /// <para><b>Deliberately no number for "the three heaviest" here.</b> The last time this comment
    /// carried one it was stale, and read as a comfortable margin while the real figure was
    /// forty-nine characters from dropping a pack. <c>SkillBudgetTests</c> computes it.</para>
    /// </remarks>
    public const int MaxCharacters = 21_000;

    /// <summary>
    /// How much of <see cref="MaxCharacters"/> must remain unused by the three heaviest packs.
    ///
    /// <para>Pinned by <c>SkillBudgetTests</c>. Without it the budget is a cliff: every edit passes
    /// until one does not, and the one that does not is a silently missing pack rather than a failure
    /// anyone can see. With it, the pack that would consume the last of the margin fails first.</para>
    /// </summary>
    public const int MinimumHeadroom = 1_000;

    private readonly IReadOnlyList<StrategySkill> _skills;

    private StrategySkillLibrary(IReadOnlyList<StrategySkill> skills) => _skills = skills;

    public IReadOnlyList<StrategySkill> All => _skills;

    /// <summary>Loads the packs embedded at build time. An unparseable one is skipped rather than thrown —
    /// a malformed skill must not take the builder down.</summary>
    public static StrategySkillLibrary Load()
    {
        var assembly = typeof(StrategySkillLibrary).Assembly;
        var skills = new List<StrategySkill>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var reader = new StreamReader(stream);
            if (Parse(reader.ReadToEnd()) is { } skill) skills.Add(skill);
        }

        return new StrategySkillLibrary(skills);
    }

    /// <summary>The skills a brief warrants, best match first, bounded by count and characters.</summary>
    public IReadOnlyList<StrategySkill> SelectFor(string? brief) => SelectFor(brief, MaxSkillsPerSession);

    /// <summary>Same selection under a caller-chosen count ceiling — the build-effort profile buys a
    /// higher (or lower) skill budget than the default. The character ceiling still applies: a Max-effort
    /// session must not rebuild the monolith the split exists to avoid.</summary>
    public IReadOnlyList<StrategySkill> SelectFor(string? brief, int maxSkills) =>
        SelectFor(brief, maxSkills, kind: null);

    /// <summary>
    /// Selection narrowed to one unit kind. A pack tagged for the other kind is not merely ranked
    /// lower — it is excluded, because the problem is not relevance but applicability: risk guidance
    /// in a visualizer session describes an API the unit does not have.
    /// </summary>
    /// <param name="kind">The kind being authored, or null to consider every pack.</param>
    public IReadOnlyList<StrategySkill> SelectFor(string? brief, int maxSkills, AuthoringKind? kind)
    {
        if (string.IsNullOrWhiteSpace(brief) || maxSkills <= 0) return [];

        var ranked = _skills
            .Where(skill => kind is not { } k || skill.AppliesTo(k))
            .Select(skill => (skill, score: skill.Score(brief)))
            .Where(candidate => candidate.score > 0)
            .OrderByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.skill.Id, StringComparer.Ordinal)   // stable: same brief, same prompt
            .Select(candidate => candidate.skill);

        var chosen = new List<StrategySkill>();
        var budget = MaxCharacters;

        foreach (var skill in ranked)
        {
            if (chosen.Count == maxSkills) break;
            if (skill.Body.Length > budget) continue;

            chosen.Add(skill);
            budget -= skill.Body.Length;
        }

        return chosen;
    }

    /// <summary>The system prompt for a session: the base pack, then whatever skills the brief warrants.</summary>
    public static string Compose(string basePack, IReadOnlyList<StrategySkill> skills)
    {
        if (skills.Count == 0) return basePack;

        var sb = new StringBuilder(basePack);
        sb.AppendLine().AppendLine()
          .AppendLine("---")
          .AppendLine()
          .AppendLine("# Loaded reference (relevant to this strategy)")
          .AppendLine();

        foreach (var skill in skills)
            sb.AppendLine(skill.Body).AppendLine();

        return sb.ToString();
    }

    /// <summary>Reads the `---` front matter (id / name / triggers) and the body after it.</summary>
    private static StrategySkill? Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;

        string? id = null, name = null;
        var triggers = Array.Empty<string>();
        var kinds = Array.Empty<AuthoringKind>();
        var body = -1;

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { body = i + 1; break; }

            var separator = lines[i].IndexOf(':');
            if (separator <= 0) continue;

            var key = lines[i][..separator].Trim();
            var value = lines[i][(separator + 1)..].Trim();

            switch (key)
            {
                case "id": id = value; break;
                case "name": name = value; break;
                case "kinds":
                    // Unparseable names are dropped rather than failing the pack: a typo should cost
                    // the narrowing, not the whole skill.
                    kinds = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(k => Enum.TryParse<AuthoringKind>(k, ignoreCase: true, out var parsed)
                            ? (AuthoringKind?)parsed
                            : null)
                        .Where(k => k is not null)
                        .Select(k => k!.Value)
                        .Distinct()
                        .ToArray();
                    break;

                case "triggers":
                    triggers = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.ToLowerInvariant())
                        .ToArray();
                    break;
            }
        }

        if (id is null || name is null || body < 0 || triggers.Length == 0) return null;

        return new StrategySkill(id, name, triggers, string.Join('\n', lines[body..]).Trim(), kinds);
    }
}
