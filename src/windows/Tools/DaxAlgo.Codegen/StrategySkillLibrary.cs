using System.Reflection;
using System.Text;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>One on-demand domain pack: what it knows, and the words that mean it is relevant.</summary>
/// <param name="Id">Stable id (the file name).</param>
/// <param name="Name">Shown in the builder's activity strip when it loads.</param>
/// <param name="Triggers">Lower-cased phrases; a brief containing any of them pulls the skill in.</param>
/// <param name="Body">The markdown the model gets.</param>
public sealed record StrategySkill(string Id, string Name, IReadOnlyList<string> Triggers, string Body)
{
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
    public const int MaxCharacters = 12_000;

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
    public IReadOnlyList<StrategySkill> SelectFor(string? brief)
    {
        if (string.IsNullOrWhiteSpace(brief)) return [];

        var ranked = _skills
            .Select(skill => (skill, score: skill.Score(brief)))
            .Where(candidate => candidate.score > 0)
            .OrderByDescending(candidate => candidate.score)
            .ThenBy(candidate => candidate.skill.Id, StringComparer.Ordinal)   // stable: same brief, same prompt
            .Select(candidate => candidate.skill);

        var chosen = new List<StrategySkill>();
        var budget = MaxCharacters;

        foreach (var skill in ranked)
        {
            if (chosen.Count == MaxSkillsPerSession) break;
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
                case "triggers":
                    triggers = value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.ToLowerInvariant())
                        .ToArray();
                    break;
            }
        }

        if (id is null || name is null || body < 0 || triggers.Length == 0) return null;

        return new StrategySkill(id, name, triggers, string.Join('\n', lines[body..]).Trim());
    }
}
