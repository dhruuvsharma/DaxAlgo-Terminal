using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What <see cref="ICliWorkspaceLauncher.Launch"/> did: whether a terminal actually opened,
/// a user-facing message either way, and where the workspace lives (empty when scaffolding failed).</summary>
public sealed record CliLaunchResult(bool Success, string Message, string WorkspacePath);

/// <summary>
/// The builder's interactive escape hatch: instead of chatting through the in-app pane, hand the
/// unit to the user's own installed agent CLI (Claude Code, Codex) in a real terminal, inside a
/// scaffolded workspace that carries the same Blocks SDK context the in-app builder sends — the
/// conventions and block index as <c>CLAUDE.md</c>/<c>AGENTS.md</c>, one card per block under
/// <c>blocks/cards/</c>, and a starter unit with its page. The vendor CLI owns its own login; no
/// credentials pass through here.
/// </summary>
public interface ICliWorkspaceLauncher
{
    /// <summary>The agent CLIs actually installed (their executable resolves on PATH) — the launch
    /// menu shows only these; an empty list hides it.</summary>
    IReadOnlyList<AgentCliAdapter> AvailableClis();

    /// <summary>Scaffolds (or refreshes) the strategy's workspace and opens <paramref name="adapter"/>'s
    /// CLI interactively in a terminal there. Never throws — every failure comes back as a message.</summary>
    CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort);
}

/// <summary>
/// Scaffolds <c>%LOCALAPPDATA%\DaxAlgo\Hyperion\&lt;strategy-id&gt;\</c> and opens the CLI in the first
/// terminal that exists: Windows Terminal → pwsh → Windows PowerShell → cmd. Guide files (the
/// conventions, the index, the cards) are refreshed on every launch so they never go stale; the user's
/// own code (<c>MyUnit.cs</c>, <c>ui/index.html</c>) is written once and never overwritten.
///
/// <para><b>The cards are files, not the guide.</b> The guide carries the conventions and the one-line
/// index; each card is its own file, and the guide tells the agent to read only the ones its code calls —
/// the same progressive disclosure the in-app builder applies, so a CLI session does not start by
/// reading every signature in the SDK.</para>
/// </summary>
public sealed class CliWorkspaceLauncher(
    ILogger<CliWorkspaceLauncher>? logger = null,
    BlockCatalog? catalog = null) : ICliWorkspaceLauncher
{
    private readonly BlockCatalog _catalog = catalog ?? BlockCatalog.Load();

    public IReadOnlyList<AgentCliAdapter> AvailableClis() =>
        [.. AgentCliAdapter.All.Where(a => AgentCliCodegenClient.ResolveOnPath(a.Executable) is not null)];

    public CliLaunchResult Launch(AgentCliAdapter adapter, string strategyId, string displayName, StrategyBuildEffort effort)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var exe = AgentCliCodegenClient.ResolveOnPath(adapter.Executable);
        if (exe is null)
            return new(false, $"{adapter.DisplayName} isn't installed — {adapter.Executable} doesn't resolve on PATH.", string.Empty);

        string workspace;
        try
        {
            workspace = Scaffold(strategyId, displayName, effort);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Could not scaffold the Hyperion workspace for {Id}", strategyId);
            return new(false, $"Couldn't scaffold the workspace: {ex.Message}", string.Empty);
        }

        var terminal = StartTerminal(adapter.Executable, exe, workspace);
        if (terminal is null)
        {
            return new(false,
                $"Workspace ready at {workspace}, but no terminal could be opened — open one there yourself and run `{adapter.Executable}`.",
                workspace);
        }

        logger?.LogInformation(
            "Opened {Cli} via {Terminal} in the Hyperion workspace {Workspace}", adapter.DisplayName, terminal, workspace);
        return new(true,
            $"Opened {adapter.DisplayName} ({terminal}) in {workspace} — the Blocks conventions and cards are already in the folder.",
            workspace);
    }

    // ── scaffolding ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates/refreshes the workspace and returns its path. Guide files are overwritten each
    /// time (they mirror the app's embedded catalog); user-editable code files are written only once.</summary>
    /// <param name="baseDirectory">The folder the <c>DaxAlgo\Hyperion</c> tree goes under; the user's local
    /// application data when null. A test passes its own.</param>
    internal string Scaffold(string strategyId, string displayName, StrategyBuildEffort effort, string? baseDirectory = null)
    {
        var safeId = Sanitize(strategyId);
        var root = Path.Combine(
            baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo", "Hyperion", safeId);
        var cardsDir = Path.Combine(root, "blocks", "cards");
        Directory.CreateDirectory(cardsDir);

        var name = string.IsNullOrWhiteSpace(displayName) ? strategyId : displayName.Trim();

        // Always refreshed — these mirror what ships inside the app, and staleness is a real bug: this
        // workspace went on teaching a retired contract long after the pane had moved on.
        var guide = GuideMarkdown(strategyId, name, effort);
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), guide);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), guide);
        File.WriteAllText(Path.Combine(root, "system-prompt.md"), _catalog.SharedContext);
        File.WriteAllText(Path.Combine(root, "README.md"), Readme(name, strategyId, effort));
        File.WriteAllText(Path.Combine(root, "blocks", "index.md"), _catalog.Index);
        foreach (var id in _catalog.Ids)
            File.WriteAllText(Path.Combine(cardsDir, $"{Sanitize(id)}.md"), _catalog.Card(id));

        // Written once — a relaunch must never clobber the user's work-in-progress.
        WriteIfAbsent(Path.Combine(root, ".claude", "settings.json"), SettingsJson);
        WriteIfAbsent(Path.Combine(root, "MyUnit.cs"), BlockStarters.Strategy);
        WriteIfAbsent(Path.Combine(root, "ui", "index.html"), BlockStarters.Page);

        return root;
    }

    private static void WriteIfAbsent(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    /// <summary>A strategy id is user input and becomes a folder name — same replacement rule as the
    /// session store, so an id can never escape the Hyperion directory.</summary>
    private static string Sanitize(string strategyId)
    {
        var safe = new string((strategyId ?? string.Empty).Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray());
        return safe.Length == 0 ? "strategy" : safe;
    }

    // ── the interactive terminal ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the CLI interactively (never headless <c>-p</c>) in the first terminal that resolves on
    /// PATH: Windows Terminal → pwsh → Windows PowerShell → cmd. Returns the launcher used, or null when
    /// nothing could be started. Windows Terminal spawns its command via CreateProcess, which cannot run
    /// an npm <c>.cmd</c> shim directly — those are wrapped in <c>cmd /k</c>; the shells run the command
    /// by name themselves, so they need no wrapping.
    /// </summary>
    private string? StartTerminal(string executable, string resolvedExe, string workspace)
    {
        var direct = resolvedExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable
            : $"cmd /k {executable}";

        var candidates = new (string Launcher, string Arguments)[]
        {
            ("wt", $"-d \"{workspace}\" {direct}"),
            ("pwsh", $"-NoExit -Command \"{executable}\""),
            ("powershell", $"-NoExit -Command \"{executable}\""),
            ("cmd", $"/k {executable}"),
        };

        foreach (var (launcher, arguments) in candidates)
        {
            var launcherPath = AgentCliCodegenClient.ResolveOnPath(launcher);
            if (launcherPath is null) continue;

            try
            {
                using var started = Process.Start(new ProcessStartInfo(launcherPath)
                {
                    Arguments = arguments,
                    WorkingDirectory = workspace,
                    UseShellExecute = true,
                });
                if (started is not null) return launcher;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Terminal launcher {Launcher} failed; trying the next", launcher);
            }
        }

        return null;
    }

    // ── workspace content ───────────────────────────────────────────────────────────────────────────

    /// <summary>The author-facing guide (written as both <c>CLAUDE.md</c> and <c>AGENTS.md</c>, so
    /// Claude Code and Codex both pick it up): a short orientation, then the conventions and the index —
    /// the same prefix the in-app builder sends. Cards stay in their own files.</summary>
    private string GuideMarkdown(string strategyId, string displayName, StrategyBuildEffort effort)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {displayName} — DaxAlgo unit workspace (Blocks SDK)");
        sb.AppendLine();
        sb.AppendLine($"Scaffolded by DaxAlgo Terminal's Hyperion for `{strategyId}` at build effort **{effort.Wire()}**. " +
                      "You are writing a DaxAlgo Terminal unit: ONE public C# class implementing `IUnit`, and the unit's " +
                      "own web page in `ui/`. It is a strategy if it uses the `orders` block, otherwise a visualizer. " +
                      "C# does the data, maths, logic, orders and network; the page only draws and sends intents back.");
        sb.AppendLine();
        sb.AppendLine("## This folder");
        sb.AppendLine();
        sb.AppendLine("- `MyUnit.cs` — the starter unit. Grow it or replace it; helper types go in more `.cs` files.");
        sb.AppendLine("- `ui/index.html` — the unit's page. Add `.js` and `.css` beside it. The look is entirely yours.");
        sb.AppendLine("- `blocks/index.md` — one line per block (also below).");
        sb.AppendLine("- `blocks/cards/<block>.md` — each block's calls, what it needs and its limits. **Read only the " +
                      "cards for the blocks your code calls**; the index says which is which.");
        sb.AppendLine("- `system-prompt.md` — the conventions and index exactly as the in-app builder sends them.");
        sb.AppendLine();
        sb.AppendLine("## Getting it into the terminal");
        sb.AppendLine();
        sb.AppendLine("In DaxAlgo Terminal open Hyperion, put each file in the Code tab under the same path (`MyUnit.cs`, " +
                      "`ui/index.html`, …) and press **Compile & Register**. The same scan applies as to any unit: the " +
                      "network is allowed; files, processes, threads, reflection emit and assembly loading are refused. " +
                      "Registering writes a `.daxalgostrategy` or `.daxalgovisualizer` and keeps it for the next start.");
        sb.AppendLine();
        sb.AppendLine("Handing files back as text instead: one fenced block per file with its path on the first line, " +
                      "as the Output section below describes.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(_catalog.Conventions);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(_catalog.Index);
        return sb.ToString();
    }

    private static string Readme(string displayName, string strategyId, StrategyBuildEffort effort) => $"""
        # {displayName} (Hyperion workspace)

        A DaxAlgo Terminal unit workspace for `{strategyId}`, scaffolded at build effort `{effort.Wire()}`,
        written against the Blocks SDK.

        1. Work with your agent CLI here — `CLAUDE.md` / `AGENTS.md` carry the conventions and the block
           index; `blocks/cards/` holds one card per block.
        2. The unit is `MyUnit.cs`; its page is `ui/index.html`.
        3. When it's ready, put the files into DaxAlgo Terminal → Hyperion → Code tab under the same paths
           and press Compile & Register. Nothing runs until you do.

        Regenerating: launching the CLI from the terminal again refreshes the guide and card files but never
        touches your `.cs` files or your page.
        """;

    /// <summary>A minimal, benign Claude Code project-settings file with one demonstrative echo hook —
    /// present so workspace hooks are visibly wired, harmless so it can never surprise anyone.</summary>
    private const string SettingsJson = """
        {
          "$comment": "DaxAlgo Hyperion workspace settings. The hook below is a benign example (it only echoes) - extend or delete it as you like.",
          "hooks": {
            "SessionStart": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "echo DaxAlgo unit workspace ready - read CLAUDE.md, then only the block cards your code uses."
                  }
                ]
              }
            ]
          }
        }
        """;
}
