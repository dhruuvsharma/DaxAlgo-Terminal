using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What <see cref="ICliWorkspaceLauncher.Launch"/> did: whether a terminal actually opened,
/// a user-facing message either way, and where the workspace lives (empty when scaffolding failed).</summary>
public sealed record CliLaunchResult(bool Success, string Message, string WorkspacePath);

/// <summary>
/// The builder's interactive escape hatch: instead of chatting through the in-app pane, hand the
/// strategy to the user's own installed agent CLI (Claude Code, Codex) in a real terminal, inside a
/// scaffolded workspace that carries the same context the in-app builder would have sent — the system
/// prompt as <c>CLAUDE.md</c>/<c>AGENTS.md</c>, the domain skill packs under <c>.claude/skills/</c>,
/// and a starter project. The vendor CLI owns its own login; no credentials pass through here.
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
/// Scaffolds <c>%LOCALAPPDATA%\DaxAlgo\VibeQuant\&lt;strategy-id&gt;\</c> and opens the CLI in the first
/// terminal that exists: Windows Terminal → pwsh → Windows PowerShell → cmd. Guide files (the context
/// pack, the skills) are refreshed on every launch so they never go stale; the user's own code
/// (<c>MyStrategy.cs</c>, the project files) is written once and never overwritten.
/// </summary>
public sealed class CliWorkspaceLauncher(
    StrategyContextPack pack,
    StrategySkillLibrary skills,
    ILogger<CliWorkspaceLauncher>? logger = null) : ICliWorkspaceLauncher
{
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
            logger?.LogWarning(ex, "Could not scaffold the Vibe Quant workspace for {Id}", strategyId);
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
            "Opened {Cli} via {Terminal} in the Vibe Quant workspace {Workspace}", adapter.DisplayName, terminal, workspace);
        return new(true,
            $"Opened {adapter.DisplayName} ({terminal}) in {workspace} — the context pack and skills are already in the folder.",
            workspace);
    }

    // ── scaffolding ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates/refreshes the workspace and returns its path. Guide files are overwritten each
    /// time (they mirror the app's embedded pack); user-editable code files are written only once.</summary>
    private string Scaffold(string strategyId, string displayName, StrategyBuildEffort effort)
    {
        var safeId = Sanitize(strategyId);
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo", "VibeQuant", safeId);
        var skillsDir = Path.Combine(root, ".claude", "skills");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(skillsDir);

        var name = string.IsNullOrWhiteSpace(displayName) ? strategyId : displayName.Trim();

        // Always refreshed — these mirror what ships inside the app, and staleness is a real bug.
        var guide = GuideMarkdown(strategyId, name, effort, safeId);
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), guide);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), guide);
        File.WriteAllText(Path.Combine(root, "system-prompt.md"), pack.SystemPrompt);
        File.WriteAllText(Path.Combine(root, "README.md"), Readme(name, strategyId, effort));
        foreach (var skill in skills.All)
            File.WriteAllText(Path.Combine(skillsDir, $"{Sanitize(skill.Id)}.md"), skill.Body);

        // Written once — a relaunch must never clobber the user's work-in-progress.
        WriteIfAbsent(Path.Combine(root, ".claude", "settings.json"), SettingsJson);
        WriteIfAbsent(Path.Combine(root, "MyStrategy.cs"), StarterSource);
        WriteIfAbsent(Path.Combine(root, "GlobalUsings.cs"), GlobalUsings);
        WriteIfAbsent(Path.Combine(root, $"{safeId}.csproj"), Csproj);

        return root;
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    /// <summary>A strategy id is user input and becomes a folder name — same replacement rule as the
    /// session store, so an id can never escape the VibeQuant directory.</summary>
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
    /// Claude Code and Codex both pick it up): a short orientation, then the full context pack — the
    /// same system prompt the in-app builder sends, so the CLI works from identical knowledge.</summary>
    private string GuideMarkdown(string strategyId, string displayName, StrategyBuildEffort effort, string safeId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {displayName} — DaxAlgo strategy-authoring workspace");
        sb.AppendLine();
        sb.AppendLine($"Scaffolded by DaxAlgo Terminal's AI Strategy Builder (\"Vibe Quant\") for strategy id " +
                      $"`{strategyId}` at build effort **{effort.Wire()}**. You are writing a DaxAlgo Terminal " +
                      "strategy plugin: an `IBacktestStrategy` kernel (required), plus an `ITradingStrategy` " +
                      "descriptor and a live view-model (`LiveSignalStrategyViewModelBase`) for a catalog card. " +
                      "A view is optional — write one in code only (no XAML), or omit it and the host composes " +
                      "the default window from the descriptor's `DataRequirement`.");
        sb.AppendLine();
        sb.AppendLine("## This folder");
        sb.AppendLine();
        sb.AppendLine("- `MyStrategy.cs` — the starter skeleton. Grow it or replace it; helpers go in more files.");
        sb.AppendLine($"- `{safeId}.csproj` + `GlobalUsings.cs` — compile surface via the `DaxAlgo.Sdk` / " +
                      "`DaxAlgo.Sdk.Wpf` NuGet packages (compile-time only; the host shares its own contract " +
                      "assemblies at runtime).");
        sb.AppendLine("- `system-prompt.md` — the raw context pack (duplicated below), the complete authoring contract.");
        sb.AppendLine("- `.claude/skills/` — DaxAlgo's domain packs (order flow, quant math, risk & exits, the live " +
                      "window, instruments & data). Read the ones this strategy touches before writing signal code.");
        sb.AppendLine();
        sb.AppendLine("## Output contract (how code gets back into the terminal)");
        sb.AppendLine();
        sb.AppendLine("When handing files back, emit one ```csharp fenced block per file, each starting with a " +
                      "`// file: <Name>.cs` line. In the app: paste the files into the AI Strategy Builder's Code " +
                      "tab and press **Compile & Register** — the same policy scan applies to this code as to any " +
                      "plugin (file, network, process, registry or reflection-emit access never compiles).");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(pack.SystemPrompt);
        return sb.ToString();
    }

    private static string Readme(string displayName, string strategyId, StrategyBuildEffort effort) => $"""
        # {displayName} (Vibe Quant workspace)

        A DaxAlgo Terminal strategy-authoring workspace for `{strategyId}`, scaffolded at build effort
        `{effort.Wire()}`.

        1. Work with your agent CLI here — `CLAUDE.md` / `AGENTS.md` carry the full authoring contract,
           and `.claude/skills/` holds the domain reference packs.
        2. The strategy skeleton is `MyStrategy.cs`; the `.csproj` gives your IDE the SDK compile surface.
        3. When it's ready, paste the file set into DaxAlgo Terminal → AI Strategy Builder → Code tab and
           press Compile & Register. Nothing runs until you do — the terminal's policy scan and consent
           gate apply to this code like any other plugin.

        Regenerating: launching the CLI from the terminal again refreshes the guide files but never touches
        your `.cs` / `.csproj` files.
        """;

    /// <summary>A minimal, benign Claude Code project-settings file with one demonstrative echo hook —
    /// present so workspace hooks are visibly wired, harmless so it can never surprise anyone.</summary>
    private const string SettingsJson = """
        {
          "$comment": "DaxAlgo Vibe Quant workspace settings. The hook below is a benign example (it only echoes) - extend or delete it as you like.",
          "hooks": {
            "SessionStart": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "echo DaxAlgo strategy workspace ready - read CLAUDE.md for the authoring contract."
                  }
                ]
              }
            ]
          }
        }
        """;

    /// <summary>The namespaces the in-app Roslyn compiler imports automatically — mirrored here so the
    /// same starter file compiles in an IDE against the SDK packages.</summary>
    private const string GlobalUsings = """
        // The in-app strategy compiler imports these for you; this file mirrors that for IDE builds.
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using TradingTerminal.Core.Backtest;
        global using TradingTerminal.Core.Domain;
        global using TradingTerminal.Core.MarketData;
        global using TradingTerminal.Core.Strategies.Parameters;
        global using TradingTerminal.Core.Time;
        global using TradingTerminal.Core.Trading;
        """;

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <!-- Must match the host: DaxAlgo Terminal's Windows tree is net9.0-windows7.0. -->
            <TargetFramework>net9.0-windows7.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <LangVersion>latest</LangVersion>
            <EnableWindowsTargeting>true</EnableWindowsTargeting>
            <UseWPF>true</UseWPF>
          </PropertyGroup>

          <ItemGroup>
            <!-- Compile-time surface only (ExcludeAssets=runtime): at runtime the host shares its own
                 contract assemblies into the plugin's load context, so never ship TradingTerminal.* /
                 DaxAlgo.Sdk* copies next to the strategy. DaxAlgo.Sdk.Wpf bundles DaxAlgo.Sdk plus the
                 WPF live-window base types. -->
            <PackageReference Include="DaxAlgo.Sdk.Wpf" Version="0.2.0-alpha" ExcludeAssets="runtime" />
          </ItemGroup>

        </Project>
        """;

    /// <summary>The same starter skeleton the in-app builder opens with (kept verbatim in both places so
    /// the two entry points never teach a different contract).</summary>
    private const string StarterSource = """
        // Authored strategy. The following namespaces are imported for you:
        //   System, System.Collections.Generic, System.Linq, System.Threading(.Tasks),
        //   TradingTerminal.Core.Domain / Trading / Time / Backtest / MarketData,
        //   TradingTerminal.Core.Strategies.Parameters
        //
        // Rules: define exactly ONE public class implementing IBacktestStrategy with a
        // public (Contract) constructor. Optionally add a static Schema and a static
        // Create(Contract, StrategyParameters) to expose tunable parameters in the UI.
        // Helpers may live in additional files (the + button on the file list).

        public sealed class MyStrategy : IBacktestStrategy
        {
            public static StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
                StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1));

            public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
                new MyStrategy(contract, p.GetInt("lookback"), p.GetDouble("threshold"));

            private readonly Contract _contract;
            private readonly int _lookback;
            private readonly double _threshold;

            public MyStrategy(Contract contract) : this(contract, 20, 1.5) { }

            public MyStrategy(Contract contract, int lookback, double threshold)
            {
                _contract = contract;
                _lookback = lookback;
                _threshold = threshold;
            }

            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;

            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
            {
                // Your signal logic here. Submit orders via
                // router.PlaceOrderAsync(new OrderRequest(...)). _contract names the instrument.
                if (_lookback <= 0 || _threshold <= 0 || _contract is null) return Task.CompletedTask;
                return Task.CompletedTask;
            }

            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
                => Task.CompletedTask;
        }
        """;
}
