<#
.SYNOPSIS
    Generates the AI strategy-authoring context from ONE canonical source, for all three consumers of
    the AI Strategy Builder: the in-app pane's system prompt (sdk/ai-context/daxalgo-strategy-context.md),
    the `daxalgo strategy ai` CLI (same file), and the `dotnet new daxalgo-strategy` scaffold's
    CLAUDE.md / AGENTS.md (so a Claude Code / Codex / Cursor session opened in a scaffold shares the exact
    same SDK contract).

.DESCRIPTION
    ASSEMBLED, not hand-maintained, so nothing drifts from the code or from each other: the engine
    contract and hard rules are authored ONCE here and injected into every output; the SDK version and
    the worked-example kernel are read from source (SdkInfo.cs, the template kernel). Byte-stable across
    runs (the only stamp is SdkInfo.Version), so CI diffs the committed files against a fresh generation.

    Outputs:
      sdk/ai-context/daxalgo-strategy-context.md                    (in-app pane + CLI system prompt)
      templates/content/daxalgo-strategy/CLAUDE.md   (== AGENTS.md) (the scaffold's agent context)

    ASCII-ONLY, sources and outputs alike. Windows PowerShell 5.1 reads a BOM-less file as ANSI while
    pwsh 7 reads it as UTF-8, so a single non-ASCII character here (or in the template kernel this reads)
    makes the output depend on which host ran it -- which is exactly how the committed pack ended up
    double-encoded. Keep every string in this file, and every comment in the template kernel, ASCII.
#>
[CmdletBinding()]
param(
    [string] $RepoRoot
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Read-Source([string] $relative) {
    $path = Join-Path $RepoRoot $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "gen-ai-context: missing source '$relative'." }
    return [System.IO.File]::ReadAllText($path)
}

function Write-Utf8Lf([string] $path, [string] $content) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    # Normalize to LF + a single trailing newline so output is byte-stable across runs/platforms.
    $normalized = ($content -replace "`r`n", "`n").TrimEnd() + "`n"
    [System.IO.File]::WriteAllText($path, $normalized, (New-Object System.Text.UTF8Encoding($false)))
    return $normalized.Length
}

function Assert-Ascii([string] $path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $bad = @($bytes | Where-Object { $_ -gt 127 }).Count
    if ($bad -gt 0) {
        throw "gen-ai-context: '$path' has $bad non-ASCII byte(s). Keep the generator and the template kernel ASCII-only (see the header note)."
    }
}

# -- The two things that must track the code ------------------------------------------------------
$sdkInfo = Read-Source 'src/windows/Sdk/DaxAlgo.Sdk/SdkInfo.cs'
if ($sdkInfo -notmatch 'Version\s*=\s*"([^"]+)"') { throw "gen-ai-context: could not read SdkInfo.Version." }
$sdkVersion = $Matches[1]

$workedExample = (Read-Source 'templates/content/daxalgo-strategy/DaxNewStrategy/Engine/DaxNewStrategyKernel.cs').TrimEnd()

# -- Canonical fragments (authored ONCE, injected into every output) --------------------------------
$engineContract = @"
A strategy is an ``IBacktestStrategy`` - the host calls these as market events arrive:

``````csharp
public interface IBacktestStrategy
{
    Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct);
    Task OnDepthAsync(DepthSnapshot d, IClock c, IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
    Task OnTradeAsync(TradePrint t, IClock c, IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
    Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct);
    Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct);
}

public sealed record Tick(DateTime TimestampUtc, double Bid, double Ask, long BidSize, long AskSize);

public interface IClock { DateTime UtcNow { get; } }   // NEVER DateTime.UtcNow - backtests replay the past

public interface IOrderRouter
{
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
    IObservable<OrderEvent> OrderEvents { get; }
}

public sealed record OrderRequest(
    string ClientOrderId,        // caller-generated IDEMPOTENCY key - unique per order
    Contract Contract,
    OrderSide Side,              // Buy | Sell
    OrderType Type,              // Market | Limit | Stop | StopLimit
    long Quantity,
    double? LimitPrice = null,   // required for Limit/StopLimit
    double? StopPrice = null,    // required for Stop/StopLimit
    TimeInForce TimeInForce = TimeInForce.Day);
``````

Depth (``OnDepthAsync``) and the trade tape (``OnTradeAsync``) are opt-in - override them only if your
strategy needs L2 or prints, and declare the matching ``DataRequirement``.
"@

$hardRules = @"
1. **All state in fields.** One instance per run - no ``static`` mutable state, no shared state.
2. **Time only via ``IClock``** (the ``clock`` parameter). Never ``DateTime.UtcNow`` / ``DateTime.Now`` -
   a backtest replays historical time and the wall clock would be meaningless.
3. **Orders only via ``IOrderRouter``**, each with a **unique ``ClientOrderId``** (suffix a sequence
   counter so two orders at the same timestamp don't collide).
4. **Flatten in ``OnEndAsync``** - close any open position so the run realizes its P&L.
5. **Warm up** before trading (e.g. skip until enough ticks/bars seen); guard against zero/negative
   prices; keep ``OnTickAsync`` allocation-light (it runs per tick).
6. **No file, network, registry, process, or reflection-emit access.** The host statically scans the
   compiled code and **blocks** any of these before it runs - a strategy that uses them will be refused,
   not merely warned. A strategy consumes market data and emits orders; nothing else.
"@

$parameters = @"
Expose tunables with a static schema + a static ``Create`` factory; the backtest sweep and the live
window render an editor automatically:

``````csharp
public static StrategyParameterSchema Schema { get; } = new(
    StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
    StrategyParameter.Number("threshold", "Entry threshold", 1.5, min: 0.1, max: 10, step: 0.1),
    StrategyParameter.Bool("useTrail", "Trailing stop", false));

public static IBacktestStrategy Create(Contract contract, StrategyParameters p) =>
    new MyKernel(contract, p.GetInt("lookback"), p.GetDouble("threshold"), p.GetBool("useTrail"));
``````
"@

# -- Output 1: the pack (in-app pane + CLI system prompt) -------------------------------------------
$pack = @"
# DaxAlgo Terminal - strategy authoring context (SDK $sdkVersion)

You are writing a trading strategy for **DaxAlgo Terminal**, a WPF multi-broker terminal. A strategy is
pure signal logic that consumes market data and emits orders through a router. This document is the
complete contract; follow it exactly. **It targets SDK ``$sdkVersion`` - code you write must compile
against that SDK.**

> This build is **data / signals only** - there is no live order-execution path. Orders you place are
> simulated by the backtest engine and surfaced as signals. Do not attempt real trading.

---

## The engine contract

$engineContract

## Hard rules (a generated strategy that breaks any of these is wrong)

$hardRules

## Parameters (optional - makes the strategy tunable in the UI)

$parameters

## Data-requirement flags

``StrategyDataRequirement`` is a ``[Flags]`` enum: ``L1`` (best bid/ask), ``Bars``, ``Depth`` (L2),
``TradeTape`` (prints). Default is ``L1 | Bars``. Declare exactly what you consume - the host starts
those pumps and only offers brokers that can supply them.

## Quant cheatsheet (numerically-stable forms)

- **EMA(period)**: ``ema += 2.0 / (period + 1) * (x - ema)`` - seed ``ema = x`` on the first sample.
- **Rolling mean/var**: keep a fixed-size buffer or Welford's online variance; never re-sum a window.
- **Mid price**: ``(bid + ask) / 2`` - reject when ``<= 0``.
- **Returns**: log return ``Math.Log(p / prevP)`` for stability across scales.
- **Z-score**: ``(x - mean) / Math.Max(1e-9, stdev)`` - floor the denominator.

## Memory-safety (only if you add a live window / your own stream handling)

Bounded, batch-drained channels (never unbounded, never one-UI-marshal-per-item); one coalesced redraw
on a ``DispatcherTimer`` (not a redraw per event); dispose timers + subscriptions at close; bound every
history buffer. ``LiveSignalStrategyViewModelBase`` already does this - only your additions need care.

---

## Worked example - the demo kernel (verbatim from the ``dotnet new daxalgo-strategy`` template)

This compiles and backtests as-is; it is the shape to imitate.

``````csharp
$workedExample
``````

---

## OUTPUT CONTRACT (a) - files, in fenced blocks (in-app AI builder)

The in-app builder compiles what you write through Roslyn, in-process, and shows it to the user in a
file editor. Answer with **one fenced C# block per file**, each starting with a ``// file:`` header:

``````
// file: MyStrategy.cs
public sealed class MyStrategy : IBacktestStrategy { ... }
``````

- **Split the work across files when it helps**: the kernel in one, indicators/helpers in others. One
  file is fine for a simple strategy; do not invent files you don't need.
- Exactly **one** public class implementing ``IBacktestStrategy``, with a public ``(Contract)``
  constructor. Helper classes may live in any file. Optionally add a static ``Schema`` and a static
  ``Create(Contract, StrategyParameters)`` for tunable parameters.
- **Do NOT** write a namespace or ``using`` directives - these are ambient (already imported):
  ``System``, ``System.Collections.Generic``, ``System.Linq``, ``System.Threading``,
  ``System.Threading.Tasks``, ``TradingTerminal.Core.Domain``, ``TradingTerminal.Core.Trading``,
  ``TradingTerminal.Core.Time``, ``TradingTerminal.Core.Backtest``, ``TradingTerminal.Core.MarketData``,
  ``TradingTerminal.Core.Strategies.Parameters``.
- No file/network/process/reflection-emit APIs (they are blocked - the code will refuse to compile).
- **Return the COMPLETE file set every time**, including files you did not change. The editor replaces
  its contents with what you send; a partial answer deletes the rest.
- A short sentence of prose before the blocks is welcome. Keep it to what the user needs to know.

### Ask before you guess

If the request is ambiguous in a way that changes the strategy - the instrument or asset class, the
timeframe, the entry/exit rule, position sizing, risk limits, which data it needs (L1 / bars / depth /
tape) - **reply with your questions and NO code block**. That is a normal turn: the builder shows your
questions to the user and sends their answer back to you. Ask once, concisely (2-4 questions), then
write the strategy. Do not ask about things you can reasonably default, and do not ask twice.

### Compiler errors come back to you

If the code does not compile, the builder sends you the compiler's own diagnostics (with file and line)
and asks for the corrected file set. Fix the actual error; do not restate the code unchanged.

## OUTPUT CONTRACT (b) - full plugin project (template / CLI)

When working inside a scaffold from ``dotnet new daxalgo-strategy`` (its ``CLAUDE.md``/``AGENTS.md``
carry the same rules), you edit files rather than emit blocks:

- Put ALL strategy math in ``<Name>/Engine/<Name>Kernel.cs`` (the ``IBacktestStrategy``).
- Keep the plugin entry point (``<Name>Plugin.cs``), the ``plugin.json`` manifest, and - for a
  ``--ui`` scaffold - the view-model (on ``LiveSignalStrategyViewModelBase``) + window.
- Grow ``<Name>.Tests/`` with real invariants; ``dotnet build`` + ``dotnet test`` must stay green.
- Never reference ``TradingTerminal.*`` projects or ship host DLLs - the ``DaxAlgo.Sdk`` package
  (``ExcludeAssets="runtime"``) is the whole surface.

---

*Generated by ``build/gen-ai-context.ps1`` for SDK ``$sdkVersion``. Do not hand-edit - change the
sources (SdkInfo.cs, the template kernel, this generator) and regenerate.*
"@

# -- Output 2: the scaffold's CLAUDE.md / AGENTS.md -------------------------------------------------
# Uses the SAME $engineContract / $hardRules as the pack, so they can't drift. DaxNewStrategy is the
# template sourceName; `dotnet new` substitutes the user's project name on scaffold.
$templateClaude = @"
# DaxNewStrategy - DaxAlgo Terminal strategy plugin

This repo is a **strategy plugin for DaxAlgo Terminal** (a WPF multi-broker trading terminal),
scaffolded by ``dotnet new daxalgo-strategy``. It compiles against the **DaxAlgo.Sdk** NuGet package
(SDK ``$sdkVersion``) and is loaded by the terminal at runtime from its ``plugins/`` folder - the host
never recompiles. This file is the working context for an AI coding agent; everything below is
load-bearing. (It is generated from the same source as the in-app builder's context, so it can't drift -
see ``build/gen-ai-context.ps1``.)

## What a strategy is here

- **``DaxNewStrategy/Engine/DaxNewStrategyKernel.cs``** - the strategy itself: an ``IBacktestStrategy``
  receiving ticks and placing orders through ``IOrderRouter``. ALL strategy math lives here.
- **``DaxNewStrategy/DaxNewStrategyPlugin.cs``** - the ``IStrategyPlugin`` entry point (the host finds
  exactly one public implementation per assembly) plus the catalog descriptor. Registration is plain
  ``IServiceCollection`` calls.
- **``DaxNewStrategy/plugin.json``** - read by the host BEFORE any code loads: id, version, and
  ``targetSdkVersion`` (must stay compatible with the host SDK; pre-1.0 = exact major.minor).
- **``DaxNewStrategy.Tests/``** - offline harness driving the kernel with synthetic ticks through a
  recording router. Keep it green; grow it with the strategy's invariants.
- **(``--ui`` scaffolds only)** ``DaxNewStrategyViewModel.cs`` + ``DaxNewStrategyWindow.xaml(.cs)`` - a
  live strategy window. The VM derives from ``LiveSignalStrategyViewModelBase`` (host-provided: instrument
  picker, warm-up, start/stop, the signal feed, presets, Activity Log) and supplies just
  ``DataRequirement`` + ``BuildStrategy(contract)`` - which returns the SAME kernel the backtest runs, so
  live and backtest can't diverge. The plugin then references ``DaxAlgo.Sdk.Wpf`` (not ``DaxAlgo.Sdk``)
  and registers the VM + window + a ``StrategyFactoryRegistration``. A headless scaffold has none of this.

## The engine contract (from DaxAlgo.Sdk - do not redeclare)

$engineContract

## Hard rules

$hardRules

## Parameters (optional)

$parameters

## Commands

``````powershell
dotnet build DaxNewStrategy.slnx          # build plugin + tests
dotnet test  DaxNewStrategy.slnx          # offline kernel harness
./pack-plugin.ps1                          # -> DaxNewStrategy.daxplugin (integrity-indexed zip)
``````

Install into the terminal: **Plugins -> Manage strategy plugins... -> Install plugin...** and pick the
``.daxplugin`` (or the built ``.dll`` for a quick dev drop-in), then restart the terminal. It appears in
Backtest Studio and the ``daxalgo-backtest`` CLI under the id in ``plugin.json``. Loading problems show
in the Plugin Manager and the terminal's Activity Log with a classified reason.

## Definition of done for a change

Build green, tests green (including at least one test that exercises the changed behaviour),
``./pack-plugin.ps1`` produces a package, and the kernel still flattens at end of run.

<!-- Generated by build/gen-ai-context.ps1 for SDK $sdkVersion. Do not hand-edit - change the sources and regenerate. -->
"@

# -- Write -----------------------------------------------------------------------------------------
$packPath  = Join-Path $RepoRoot 'sdk/ai-context/daxalgo-strategy-context.md'
$claudePath = Join-Path $RepoRoot 'templates/content/daxalgo-strategy/CLAUDE.md'
$agentsPath = Join-Path $RepoRoot 'templates/content/daxalgo-strategy/AGENTS.md'

$packLen = Write-Utf8Lf $packPath $pack
$claudeLen = Write-Utf8Lf $claudePath $templateClaude
Write-Utf8Lf $agentsPath $templateClaude | Out-Null

# The whole point of the ASCII rule: a stray Unicode character makes the bytes depend on which
# PowerShell ran this. Fail loudly here rather than committing a double-encoded pack.
Assert-Ascii $packPath
Assert-Ascii $claudePath
Assert-Ascii $agentsPath

Write-Host "gen-ai-context: SDK $sdkVersion"
Write-Host "  pack        -> $packPath ($packLen chars)"
Write-Host "  CLAUDE.md   -> $claudePath ($claudeLen chars)"
Write-Host "  AGENTS.md   -> $agentsPath (== CLAUDE.md)"
