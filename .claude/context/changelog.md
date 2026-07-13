# context changelog — append-only session journal

## 2026-07-13 — #26 AI Strategy Builder v2, phase 1: chat workspace, multi-file, models
- **Multi-file authoring.** `StrategyScript` now carries `IReadOnlyList<StrategyFile>` (single-file ctor
  kept); `RoslynStrategyCompiler` parses one tree per file and `StrategyDiagnostic` gained `File`, so an
  error names the file it's in — and the auto-fix prompt can too. References widened to the full
  trusted-platform set (host assemblies incl. UI/UI.Core/Sdk/WPF), deliberately NOT the loaded-assembly
  list (plugin ALCs would bind a second Type identity).
- **Conversation, not one-shot.** New `StrategyBuildSession` owns the message thread across turns
  (follow-ups, the compiler's errors, and the model's own questions all land in one context);
  `StrategyCodegenOrchestrator` becomes its factory and keeps `BuildAsync` for the CLI/tests. A reply
  with **no code is a Question turn, not a failure** — the builder shows it and waits for the user.
- **Multi-block extraction.** `CodegenCodeExtractor.ExtractFiles` splits `// file: X.cs`-headed fences
  (skips non-C# fences; positional names; never collides). Prose stays prose.
- **Models + tokens.** `IStrategyCodegenClient` gained `Model` / `KnownModels` / `ListModelsAsync`;
  clients report `CodegenUsage` (OpenAI + Anthropic both return usage); `StrategyCodegenClientFactory.Build(providerId, model)`
  rebuilds a client on a model switch; agent CLIs take `--model`/`-m`. `AiCodegenUserFile` persists the
  choice (Pro shell layers it, like the Research file).
- **Pane rewritten (Pro shell).** Tabbed **Chat | Code | Parameters**: real multi-line composer
  (Ctrl+Enter) — the old single-line box hid everything past ~25 words and read as a length cap; the
  model's reply is now shown at all (the transcript used to be discarded); file list + per-file editor;
  live activity strip + token counter. `Tests.Pro` gained XAML smoke tests pinning both regressions.
- **Context pack fixed + un-stale.** `build/gen-ai-context.ps1` was **gitignored** (`[Bb]uild/`), so the
  `template-smoke` drift check ran a script that wasn't in the repo, and the committed pack was stale
  (0.1.0 vs SDK 0.1.1) *and* double-encoded (PS 5.1 read UTF-8 sources as ANSI). Generator + template
  kernel are now ASCII-only with an `Assert-Ascii` gate, `.gitignore` un-excludes `/build/`, and the
  pack's OUTPUT CONTRACT (a) is the multi-file + ask-questions contract.
- Phases 2 (plugin emit + hot catalog/Plugin-Manager registration) and 3 (streaming + reverse questions)
  follow. 737 headless + 8 Pro tests green.

## 2026-07-12 (later+4) — #26 phase 3: template CLAUDE/AGENTS generated from the pack source
- `gen-ai-context.ps1` factors the engine contract + hard rules + parameters into canonical fragments
  injected into BOTH the pack (`sdk/ai-context/`) AND the template's `CLAUDE.md` (== `AGENTS.md`) — one
  source of truth for the SDK contract, so the in-app builder and a `dotnet new` + agent session can't
  drift. Template files keep scaffold-specifics; all 3 outputs byte-stable; `template-smoke` drift step
  now regenerates + fails if any is stale. **#26 all 4 phases done.** Public `202ca10`, Pro `8b22408`.

## 2026-07-12 (later+3) — #26 Settings "AI providers" section (keyed providers work in-app)
- `AiKeyStore` (Login, DPAPI per-user, `ai-keys.json`) implements new Core `IAiKeyStore`;
  `CredentialStoreAiKeyResolver` (Login) = `IAiKeyResolver` over it + `{PROVIDER}_API_KEY` env fallback.
  **`AddLogin` registers both (one shared edit)** ⇒ replaces `AddStrategyCodegen`'s Null resolver, so
  OpenAI/DeepSeek/xAI/OpenRouter/Anthropic resolve once a key exists.
- Settings → "AI providers": `AiProvidersSettingsViewModel` (shared) + `AiProvidersSettingsView` ×3
  (per-provider key box + Save/Clear; agent CLIs shown detected). ×3 wiring: AddSettingsSurface +
  `OpenAiProvidersSettings` + `_Settings` menu item. Public `5232f57`, Pro `a9308d5`. Smoke 9/9.

## 2026-07-12 (later+2) — #26 in-app AI pane (P2 UI) + daxalgo CLI (P4) + DaxAlgo.Codegen extraction
- **In-app pane ×3 shells:** authoring window gained an AI Strategy Builder panel (provider picker +
  prompt + Generate). Shared `StrategyAuthoringViewModel` (Settings) drives it via `IAiStrategyBuilder`;
  context pack ships EMBEDDED (`StrategyContextPack`). Generate compiles + shows code but does NOT
  register — the user's **Compile click is the consent for model-authored code** (#23 hook), already
  scan-gated + DEV-badged. Keyless providers (agent CLIs, Ollama) work out of the box; keyed via
  `IAiKeyResolver` (Core seam, Null fallback). `AddStrategyCodegen(configuration)` ×3 shells;
  `AiCodegen` appsettings section. Public `769433c`, Pro `706ca23`.
- **`daxalgo strategy` CLI** (`DaxAlgo.StrategyTool`, global tool, 7th lockstep NuGet package):
  new/build/test/package/install/ai. `ai --provider fake` E2E verified (scaffold→build→test→package);
  real providers read `{PROVIDER}_API_KEY`. `template-smoke.yml` `cli` job. `docs/ai-strategy-builder.md`.
- **`DaxAlgo.Codegen` extraction:** Infrastructure is UseWPF=true, so the codegen pieces (clients,
  orchestrator, factory, pack, builder, DI) moved to a lean new assembly (Core + MS.Extensions only) so
  the CLI reuses them without WPF. **Types KEPT the `TradingTerminal.Infrastructure.Strategies.Authoring`
  namespace ⇒ every consumer unchanged** (Infrastructure just adds a ProjectReference; transparent —
  700 headless green via transitive ref, Pro smoke 9/9). Public `42a2dc2`, Pro pin `e0fde8a`.
- **#26 remaining:** in-app Settings "AI providers" section (CLI-detection display + key entry into the
  credential store + a credential-store `IAiKeyResolver`); Phase 3 template CLAUDE.md/AGENTS.md content
  unification; live-key validation of the CLI `ai` real-provider path.

## 2026-07-12 (later+1) — #26 AI Strategy Builder: pack (P1) + codegen backend (P2)
- **Phase 1 (context pack):** `build/gen-ai-context.ps1` GENERATES `sdk/ai-context/daxalgo-strategy-context.md`
  — the codegen system prompt (engine contract, 6 hard rules, params schema, DataRequirement, quant
  cheatsheet, memory-safety, verbatim demo kernel, + two output contracts: single-file kernel for the
  in-app pane / full plugin project for template+CLI). Can't drift (version + example read from source);
  byte-stable across runs (verified); a `template-smoke.yml` step regenerates + fails on diff.
- **Phase 2 backend:** `IStrategyCodegenClient` + `AiCodegenOptions` (Core); `StrategyCodegenOrchestrator`
  (Infrastructure) = the loop generate→compile via the SAME `IStrategyCompiler`→feed compiler errors
  back→bounded retry (**scan gate is in the shared compile step ⇒ generated P/Invoke can NEVER exit as
  success — tested**); providers `Fake`/`OpenAiCompatible`(OpenAI/DeepSeek/xAI/OpenRouter/Ollama)/
  `Anthropic`/`AgentCli`(drives `claude -p`/`codex exec` w/ timeout+kill-tree, vendor owns login);
  `CodegenCodeExtractor`; `StrategyCodegenClientFactory` (assembles usable providers from config + a
  shell key-resolver — keys stay in the Login DPAPI store, Infra can't ref it). 17 codegen test cases;
  696 headless green. Commits public `2c67dba` (pack), `cf97c24` (backend), `7700634` (factory).
- **Backend only, no DI/UI.** Remaining #26: in-app pane ×3 shells + Settings "AI providers" section
  (model-written-code CONSENT lands here per the #23 note) + `DaxAlgo.StrategyTool` global tool +
  template hand-off + `docs/ai-strategy-builder.md`.

## 2026-07-12 (later) — #24 tail: template `--ui` variant, authoring guide v2, template CI smoke
- **`dotnet new daxalgo-strategy --ui`** — a `ui` bool template symbol adds a live window: VM on
  `LiveSignalStrategyViewModelBase` + a self-contained `MetroWindow` (style-trigger visibility, no host
  converter) + `StrategyFactoryRegistration`; csproj conditionally swaps `DaxAlgo.Sdk`→`DaxAlgo.Sdk.Wpf`
  + `UseWPF` (`<!--#if (ui)-->`), plugin.cs conditionally registers the view (`#if (ui)`). **Verified E2E
  against the PUBLISHED NuGet packages**: both variants scaffold → build → test (3/3) → pack; `--ui` bin
  ships only its own dll (ExcludeAssets=runtime cascades — no MahApps/TradingTerminal leak); clean
  `.daxplugin`.
- **Authoring guide v2** — new `docs/plugin-authoring.md` (template-first: scaffold → kernel rules →
  params schema → DataRequirement/asset/paper pills → test harness → package → **pre-1.0 exact
  major.minor version policy** → sign/submit + AGPL-linking caveat → `--ui` walkthrough → memory-safety
  checklist). `docs/plugins.md` dev section trimmed to a pointer; docs/README + CLAUDE per-topic list +
  the plugin-security link updated.
- **Anti-drift**: the sample is reframed as a minimal in-tree reference; the **template** is canonical.
  New `.github/workflows/template-smoke.yml`: `in-repo` job (every PR) guards template-version==
  DaxAlgoSdkVersion, then scaffolds+builds+tests+packs BOTH variants + asserts the identity rule (no host
  DLLs in output); `published` job (schedule/dispatch) installs `DaxAlgo.Templates` from NuGet.org and
  builds with NO repo checkout (pure NuGet resolution — the clean-runner smoke).
- No `src` changes; templates/docs/workflow only. **#24 remaining after this: none of the tail** (all four
  items done). Next per epic #27: **#26** (AI Strategy Builder on the now-scan-gated Roslyn seam) → #25.

## 2026-07-12 — #23 tail: closed all four deferred flags
- **Authoring-pane gating (the real hole):** `RoslynStrategyCompiler` now scans the emitted PE with the
  SAME policy scan (new `PluginPolicyScanner.ScanImage(bytes,name)`) BEFORE `Assembly.Load` — Block-level
  authored code (P/Invoke, Process, registry, Reflection.Emit, Assembly.Load) fails the compile via an
  Error diagnostic; Warn-level surfaces as a Warning diagnostic (the pane already renders
  `result.Diagnostics`, so zero UI plumbing). `IStrategyCompiler` xml-doc rewritten (it claimed authored
  code was "no more privileged"; now mandates the scan). VM tags registered authored strategies
  "DEV (unsigned)". Shared VM (`TradingTerminal.Settings`), not ×3.
- **DEV badge on the catalog card:** `LoadedPlugin.StrategyImplementationTypes` (captured from the guard's
  staged `ITradingStrategy` descriptors) → `PluginHostContext.UnsignedStrategyTypeNames` → shell VM
  `UnsignedStrategyIds` → `UnsignedStrategyConverter` (IMultiValueConverter, shared in UI) drives a DEV
  pill on the card via MultiBinding [Id, VM set]. ×3 shells (VM + App.xaml.cs register + MainWindow.xaml).
  Attribution is reusable for #25.
- **WinVerifyTrust happy path:** `AuthenticodeSignatureInspectorTests` inspects a genuinely
  Microsoft-embedded-signed runtime binary (hostpolicy/coreclr/… next to CoreLib) and asserts
  IsSigned+IsValid+thumbprint, plus the end-to-end Curated pin-accepts / different-thumbprint-rejects.
  No cert install; early-returns if none present (this xUnit has no Assert.Skip).
- **Docs:** `docs/plugin-security.md` (full threat model — what protects you, what does NOT, the DEV
  badge, what's honestly not built) + **ADR-0009** (out-of-proc strategy host, proposed/not-scheduled) +
  plugins.md links.
- 9 new tests; 682 headless + 5 Pro green; smoke 9/9. **#23 is now essentially complete** (only the
  credential-isolation audit *notes* remain, folded into plugin-security.md).

## 2026-07-11 (latest+2) — #23 phase 3: hash-pinned trust, integrity, revocation, consent → **Curated is now the shipped default**
- **The blocker was: Curated + 9 unsigned first-party plugins = empty strategy catalog.** Dhruv's call:
  hash-pin. `build/gen-trusted-plugins.ps1` (ONE shared copy — a build script is not shell code) hashes
  every staged assembly into `plugins/plugins-trusted.json`; `PluginTrustedHashes` accepts a plugin whose
  folder matches the shipped build exactly. No certificate. Verified: Curated loads all 9 unsigned
  first-party plugins, 9/9 windows.
- Same file = the **integrity baseline, enforced in EVERY mode** (Permissive too): assembly modified,
  swapped, **added**, or removed ⇒ `PluginLoadOutcome.Tampered` + quarantine. Proven by appending one
  byte to a staged DLL: that plugin quarantines with a clear reason, the other 8 load.
  Third-party: `PluginInstaller` records sha256 at install, loader re-checks every start.
- `revoked.json` kill-list (by sha256 = one build, or by plugin id = all builds) → `Revoked` + quarantine.
- **Consent** (`IPluginConsentPrompt` + WPF `PluginConsentDialog` ×3 shells): unsigned/unpinned plugin ⇒
  the user is shown publisher/file/sha256 + the scan's capabilities + the honest "this cannot be
  sandboxed" line, and decides. Persisted **keyed by sha256** ⇒ asked once per BUILD; an update re-asks.
  **No prompt (CLI/tests/CI) ⇒ the answer is NO.** A Block-level plugin is never offered for consent
  (scan runs BEFORE the consent gate). Loaded-but-unsigned ⇒ permanent **DEV (unsigned)** badge.
- **Gate order (all before one instruction of plugin code runs):** pin/integrity → revocation →
  install-hash → IL scan → trust/consent → load → guarded registration.
- `appsettings.json` ⇒ **Curated** (shipped); `appsettings.Dev{Sim,Replay,Live}.json` ⇒ Permissive, so
  plugin authors aren't re-prompted on every rebuild (a rebuilt DLL = a new hash = a new consent).
- 19 new tests (pin/tamper/revocation/consent). 676 headless + 5 Pro green; smoke 9/9 under BOTH
  Permissive and Curated.
- **Deferred:** DEV badge on the strategy *catalog card* (needs plugin→strategy-id attribution, which
  `LoadedPlugin.RegisteredServices` doesn't carry yet) and the Roslyn authoring-pane gating (#23 item 6).

## 2026-07-11 (latest+1) — #23 phase 2: static IL policy scan
- `Infrastructure/Plugins/PluginPolicyScanner.cs` — in-box `System.Reflection.Metadata`, **no new
  dependency and no plugin code runs** (the assembly is read as DATA, so the verdict lands before the
  ALC ever sees it). **Block**: P/Invoke (metadata flag, not a typeref), `Process`, `Registry`,
  `Reflection.Emit`, `Assembly.Load*`/`AssemblyLoadContext`. **Warn**: file I/O, network I/O,
  `SetEnvironmentVariable`. Type-level rules for the unambiguous types; MEMBER-level rules for
  `Assembly.Load` / `Environment.SetEnvironmentVariable` (their declaring types are referenced by any
  `typeof(x).Assembly` / `Environment.NewLine` — a typeref rule there would false-positive everything).
- `plugin.json` gains `permissions[]`: a plugin DECLARES its Warn-level capabilities and they are
  disclosed (Plugin Manager: "uses fileIo") instead of flagged. **Block-level can never be
  self-granted** — only curation. Wired at load (`PluginLoadOutcome.BlockedByScan` → quarantine) and at
  install (refuses before the folder lands). `PluginsOptions.ScanMode` = Enforce (default) | WarnOnly | Off.
- **Tuned against the real 9 first-party plugins: ZERO Blocks** (HelixToolkit.Wpf does not P/Invoke —
  it was the feared false-Block). Only `fileIo` Warns (CSV export + Helix model IO), so the 7 that
  trip it now declare `"permissions": ["fileIo"]`. `--smoke-strategies` 9/9 with the scan enforcing.
- **Verdict cache deliberately NOT built** (the issue asks for one): measured **22.7 ms to scan all 9
  folders**, HelixToolkit included. A sha256-keyed cache would add invalidation complexity to save
  ~20ms; revisit if the scan grows. Phase 3 computes sha256 anyway for hash-pinned trust — cache there
  if ever needed.
- 10 scanner tests, all against **Roslyn-compiled fixture assemblies** (real IL, not fakes): P/Invoke,
  Process.Start, Assembly.LoadFrom, self-granting a Block (refused), declared fileIo (downgraded),
  clean strategy shapes (LINQ/typeof/Path/Environment.NewLine — no false positives), corrupt DLL,
  payload hidden in a bundled private dep (still caught).

## 2026-07-11 (latest) — #23 phase 1: plugin registrar guard + trust policy from config
- **The plugin DI seam was a credential-theft path**: `IPluginRegistrar.Services` handed every plugin
  the raw host `IServiceCollection`, and MS.DI is last-registration-wins ⇒ any loaded plugin could
  re-register `ICredentialStore` / `IBrokerSelector` / `IMarketDataStore` and intercept the broker
  session. Closed by `Infrastructure/Plugins/GuardedServiceCollection.cs`: registrations are STAGED
  and committed only if `Register()` returns cleanly (a violating plugin contributes nothing, not even
  the legitimate half); allowlist = the three real multi-registration seams (`ITradingStrategy`,
  `BacktestStrategyOption`, `StrategyFactoryRegistration`); host descriptors stay in the read view so
  `TryAdd*()` keeps its no-op semantics. New `PluginLoadOutcome.PolicyViolation` → quarantine.
- Trust policy now binds from config (`PluginsOptions`, `Plugins:TrustPolicy|TrustedThumbprints`,
  `PluginTrustPolicy.From`) instead of the `Permissive` constant hardcoded in each shell. Default
  stays Permissive — **Curated-by-default is deliberately NOT flipped**: the 9 first-party plugins are
  unsigned, so Curated would ship an empty strategy catalog. Needs the signing decision + the consent
  flow (#23 phase 3).
- Verified: 9/9 plugins still load cross-ALC (`--smoke-strategies`, Pro shell) ⇒ zero false rejections;
  50 plugin tests incl. a Roslyn-compiled **hostile plugin DLL** driven through the real loader
  (blocked → quarantined → host `IMarketDataStore` intact); 647 headless + 5 Pro green.
- Applied ×3 shells (Basic + Intermediate here, `TradingTerminal.App` in the Pro repo).

## 2026-07-11 (later) — hook suite revived + mirrored to Pro; shared memory
- **verify-on-stop.ps1 had been silently DEAD since the 2026-06-27 fork** (probed pre-fork
  `src\<Proj>\` paths; lower-layer regex matched `^src/TradingTerminal.`). Rewritten: projects
  located by glob under `src/` (covers windows + linux trees), graph extended with UI.Core +
  DaxAlgo.Sdk/.Wpf, SDK-leak regex matches the forked layout. Smoke-tested: 11 csproj inspected,
  0 violations on the clean tree. session-start.ps1 run hint fixed (App → App.Intermediate).
- Hook suite + settings.json mirrored into the Pro repo (adapted: Pro.slnx build; all Stop hooks
  also gate dirty files inside the `public/` submodule; two-root graph check).
- Cross-repo memory solved machine-locally: the Pro project's auto-memory dir is now an NTFS
  junction to this project's memory (one shared store); committed journals (this file + the Pro
  changelog) remain the repo-visible decision log.

Newest first. One short block per session that touched the context layer or shipped notable work.
(Separate from any repo CHANGELOG; this is for Claude-session continuity.)

## 2026-07-11 — one-click launcher
- `claude-launch.bat` (repo root): cd to repo + start Claude Code with an initial prompt that
  pre-loads index/symbols/deps.
- Same day: the PRO overlay repo got its own mirrored layer (private commit ddf4485) — submodule
  pin bumped to 7a6052f, Pro-only index/symbols/deps generated there, its CLAUDE.md points at
  BOTH layers, and it has its own claude-launch.bat pre-loading both. This public layer remains
  the authority for all shared-core modules.

## 2026-07-10 (later) — pointers + routing tightening applied (Dhruv approved "apply all")
- CLAUDE.md: context-layer section added (PROTOCOL.md is now the mandated per-change path);
  solution graph corrected — `Strategies.* → DaxAlgo.Sdk.Wpf ONLY` (ADR-0008), App no longer
  lists Strategies.*; project map splits `TradingTerminal.UI` vs `TradingTerminal.UI.Core`.
- `.claude/MULTI-AGENT.md` + `.claude/agents/README.md`: hard rules — no subagent for <3-file
  changes, context layer first, narrowest `.slnf` builds, no re-reads.
- 15 agent bodies got a per-module "Context layer first" line (build-runner: narrowest-slnf rule).
- Third pass (same day, Dhruv: "everything perfect"): remaining drift FIXED — CLAUDE.md Ai graph
  line (→ Core, Infrastructure), rule 9 `InMemoryLogSink` → UI.Core, per-tool paragraph re-scoped
  (paths under `src/windows/<Group>/`; BubbleChart/Surface Lab/ML menu = Pro shell only),
  ai-analyst skill row notes the Pro repo. Agent fleet corrected: `strategies` 12→9 (+ removed
  OrderFlowToxicity/OrnsteinUhlenbeck/VolatilityTargeted quirk rows), `tool-windows` lists the
  real 9 (+ AdvancedMarketRegime/BacktestStudio rows replace removed MarketRegime/InstrumentRegime),
  `ai-windows`/`app-shell`/`backtest-cli` descriptions reflect the open-core split, README fleet
  table rows updated. No known CLAUDE.md/agent drift remains as of 2026-07-10.

## 2026-07-10 — context layer initialized
- Phase 1 audit (`AUDIT.md`): 875 files / 103,104 LOC Windows tree; 49 files >400 LOC hold 31%;
  Core 58% / UI 69% / MarketData 47% of public types never name-mentioned in any doc/skill.
- Built: `gen-context.sh` (regenerator), `index.md` + `index/` (12 group files, ~880 rows),
  `symbols.md` + `symbols/` (~8k signature lines incl. interface members), `deps.json`,
  `modules/`, `glossary.md`, `adr/`, `RECIPES/`, `PROTOCOL.md`, `MAINTENANCE.md`, `tasks/`.
- Discoveries vs CLAUDE.md (docs drift, not corrected in CLAUDE.md yet):
  1. Strategy projects reference ONLY `DaxAlgo.Sdk.Wpf`; shells load them at runtime via
     `AddStrategyPlugins()` → `Infrastructure/Plugins/PluginLoader` (ALC). The plugin-marketplace
     refactor evidently rolled out to ALL 9 strategies, not just SigmaIcFlow.
  2. `LiveSignalStrategyViewModelBase`, `LiveStrategyHostServices`, `InMemoryLogSink` live in
     `TradingTerminal.UI.Core` (`src/windows/UI/`), not `TradingTerminal.UI`.
  3. `TradingTerminal.Ai` references Core+Infrastructure only (not UI/MarketData).
  4. `IBrokerClient` lives in `Core/MarketData/`, not `Core/Brokers/`.
  5. `DaxAlgo.Sdk.Wpf` is a zero-source facade csproj.
