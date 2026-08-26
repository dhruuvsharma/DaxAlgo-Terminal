using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>One role's learned estimate, as stored.</summary>
/// <param name="Role">The role's name.</param>
/// <param name="Score">Its current EMA.</param>
/// <param name="Observations">How many outcomes have been folded in.</param>
public sealed record AgentScore(string Role, double Score, int Observations);

/// <summary>
/// What the router has learned, kept across restarts.
///
/// <para>Without this the reliability EMA was constructed fresh with every view-model, which meant every
/// launch began at the neutral prior and the routing learned nothing it could keep. An estimator that
/// resets before it can warm up is not an estimator; it is a constant with extra steps — and the whole
/// argument for reward-biased routing is that the weights come from evidence.</para>
///
/// <para>Numbers only, and only about our own agents. There is nothing here about the user, their brief
/// or their code — the same rule <c>TrajectoryLog</c> follows, for the same reason.</para>
/// </summary>
public static class AgentMemory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Where it lives by default: <c>%LocalAppData%\DaxAlgo Terminal\agent-reliability.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "agent-reliability.json");

    /// <summary>
    /// Reads the estimates back, or returns a fresh one.
    ///
    /// <para>Never throws. A corrupt or absent file means starting from the neutral prior, which is
    /// exactly where a first run starts anyway — losing what was learned is a small harm next to a
    /// builder that will not open.</para>
    /// </summary>
    public static AgentReliability Load(string? path = null)
    {
        var reliability = new AgentReliability();
        var file = path ?? DefaultPath;

        try
        {
            if (!File.Exists(file)) return reliability;

            var stored = JsonSerializer.Deserialize<AgentScore[]>(File.ReadAllText(file), Json);
            if (stored is null) return reliability;

            foreach (var entry in stored)
            {
                if (!Enum.TryParse<AgentRole>(entry.Role, out var role)) continue;
                if (!double.IsFinite(entry.Score) || entry.Observations <= 0) continue;

                reliability.Restore(role, entry.Score, entry.Observations);
            }
        }
        catch (Exception)
        {
            // A fresh estimator is the safe answer to anything unreadable.
        }

        return reliability;
    }

    /// <summary>Writes the estimates. Never throws — a run that produced a strategy must not be reported
    /// as failed because a cache could not be written.</summary>
    public static void Save(AgentReliability reliability, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(reliability);

        var file = path ?? DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Only roles with real evidence behind them. Writing the neutral prior for every role would
            // make an untried agent indistinguishable on reload from one that scored exactly 0.5.
            var scores = Enum.GetValues<AgentRole>()
                .Where(role => reliability.ObservationsFor(role) > 0)
                .Select(role => new AgentScore(
                    role.ToString(), Math.Round(reliability.Of(role), 6), reliability.ObservationsFor(role)))
                .ToArray();

            File.WriteAllText(file, JsonSerializer.Serialize(scores, Json));
        }
        catch (Exception)
        {
            // Deliberately swallowed.
        }
    }
}
