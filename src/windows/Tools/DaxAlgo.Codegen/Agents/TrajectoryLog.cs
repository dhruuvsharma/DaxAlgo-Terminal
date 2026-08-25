using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// One turn, as recorded. Numbers and identifiers only.
/// </summary>
/// <param name="At">When, in UTC.</param>
/// <param name="Role">Who took the turn.</param>
/// <param name="Weights">The posterior the router used, keyed by role name.</param>
/// <param name="Reward">What the ladder gave it.</param>
/// <param name="RungsCleared">How many rungs actually passed — the denominator behind the reward.</param>
/// <param name="FailedAt">The rung that stopped it, or null.</param>
/// <param name="Codes">The finding codes, which are stable and greppable.</param>
/// <param name="InputTokens">Charged input, excluding anything read from cache.</param>
/// <param name="CachedInputTokens">Read from cache, billed at a fraction.</param>
/// <param name="OutputTokens">Generated.</param>
/// <param name="Files">How many files came back.</param>
public sealed record TrajectoryEntry(
    DateTime At,
    string Role,
    IReadOnlyDictionary<string, double> Weights,
    double Reward,
    int RungsCleared,
    string? FailedAt,
    IReadOnlyList<string> Codes,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int Files);

/// <summary>
/// A JSONL record of what the agents did and what it cost.
///
/// <para>Two jobs, and the second is the one that pays for it today. It is the trajectory store the
/// paper's skill distillation would eventually need — designed now so that stays possible, built now
/// because <b>you cannot minimise what you do not measure</b>. Per-turn token counts are the only way to
/// find out whether the six-agent split is cheaper than one long conversation, or which agent is
/// quietly burning the budget.</para>
///
/// <para><b>It records numbers and codes, never text.</b> Not the brief, not the reply, not the code.
/// Two reasons, and either alone would be enough: a user's strategy is their intellectual property and
/// has no business sitting in a log they did not ask for, and a log that swallowed model output would be
/// a channel for feeding untrusted text back into a later prompt.</para>
///
/// <para>Bounded by line count, oldest first. An append-only file on a user's machine is a slow leak,
/// and the value of a trajectory decays: the turns that matter are the recent ones, taken against the
/// model and the prompts currently in use.</para>
/// </summary>
public sealed class TrajectoryLog(string path, int maxEntries = 2000)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly Lock _gate = new();

    /// <summary>Where the file lives.</summary>
    public string Path { get; } = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A trajectory log needs a path.", nameof(path))
        : path;

    /// <summary>Appends one turn, trimming the oldest when the file is full.</summary>
    public void Append(
        AgentTurn turn,
        Verification.VerificationReport report,
        CodegenUsage? usage,
        DateTime? at = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(report);

        var entry = new TrajectoryEntry(
            at ?? DateTime.UtcNow,
            turn.Role.ToString(),
            turn.Weights.ToDictionary(pair => pair.Key.ToString(), pair => Math.Round(pair.Value, 4)),
            Math.Round(turn.Reward, 4),
            report.RungsCleared,
            report.FailedAt?.ToString(),
            [.. report.Findings.Select(f => f.Code)],
            usage?.InputTokens ?? 0,
            usage?.CachedInputTokens ?? 0,
            usage?.OutputTokens ?? 0,
            turn.Files.Count);

        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, JsonSerializer.Serialize(entry, Json) + Environment.NewLine, Encoding.UTF8);
            Trim();
        }
    }

    /// <summary>Reads it back, oldest first. A malformed line is skipped rather than throwing — a log is
    /// diagnostics, and one bad line must not cost the rest.</summary>
    public IReadOnlyList<TrajectoryEntry> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return [];

            var entries = new List<TrajectoryEntry>();
            foreach (var line in File.ReadAllLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<TrajectoryEntry>(line, Json) is { } entry)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // Skipped on purpose.
                }
            }

            return entries;
        }
    }

    /// <summary>
    /// What a run cost, and how much of it the cache absorbed.
    ///
    /// <para>The cached share is the number to watch: it is what the system-prompt split was for, and if
    /// it falls the prefix has been broken by something — a reordered block, a role appended to the
    /// shared pack — which costs money silently and shows up nowhere else.</para>
    /// </summary>
    public TrajectoryCost Cost()
    {
        var entries = Read();
        return new TrajectoryCost(
            entries.Count,
            entries.Sum(e => e.InputTokens),
            entries.Sum(e => e.CachedInputTokens),
            entries.Sum(e => e.OutputTokens));
    }

    /// <summary>Trims to the newest <c>maxEntries</c> lines.</summary>
    private void Trim()
    {
        var lines = File.ReadAllLines(Path);
        if (lines.Length <= maxEntries) return;

        File.WriteAllLines(Path, lines.Skip(lines.Length - maxEntries), Encoding.UTF8);
    }
}

/// <summary>What a set of turns cost.</summary>
public sealed record TrajectoryCost(int Turns, int InputTokens, int CachedInputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + CachedInputTokens + OutputTokens;

    /// <summary>The fraction of input read from cache. Zero when nothing was charged at all.</summary>
    public double CachedShare => InputTokens + CachedInputTokens == 0
        ? 0d
        : (double)CachedInputTokens / (InputTokens + CachedInputTokens);

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Turns} turn(s) · {TotalTokens} tokens · {CachedShare:P0} of input cached");
}
