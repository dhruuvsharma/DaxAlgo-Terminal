# context changelog — append-only session journal

## 2026-07-22 — local account access profile
- Added first-choice Debug launch profiles for Basic and Intermediate that select DevSim and use
  the existing in-memory local account before bypassing broker login into the simulated terminal.
- Made the development account gate label its temporary local profile explicitly; Release builds
  and non-development environments remain fail-closed.

## 2026-07-22 — concurrency-safe context gate
- Serialized context checks and regeneration across terminals and added content fingerprints so a
  moving working tree is never classified as deterministic stale context.
- Added exact-state verification stamps for single-flight Stop checks and routed all generator
  writes through `manage-context.ps1 sync`; legacy direct calls delegate to the locked workflow.

## 2026-07-22 — obsolete workspace residue cleanup
- Removed two unreferenced null research clients after the HTTP implementations became the sole DI
  registrations, and regenerated the Windows Pipeline index/symbol context.
- Removed the obsolete in-tree strategy generator, stale web placeholder, tracked Python bytecode,
  and local generated/cache/log/task-render residue; active projects and external strategy ownership
  are unchanged.

## 2026-07-22 — DaxAlgo theme consolidation
- Removed the Bloomberg Amber, Monochrome, Greek Marble, and Greek Obsidian bundled palettes.
- DaxAlgo Dark and DaxAlgo Light are now the only built-in bases across Basic, Intermediate, and
  consuming overlay shells; saved legacy ids safely fall back to DaxAlgo Dark.

## 2026-07-22 — current-code session and context gates
- Regenerated the Windows index/symbol layer from the current working tree and removed residual
  `.axaml` generator/manager handling after the Linux extraction.
- SessionStart now reports fast structural context health and injects Windows-only routing.
- Stop-time doc sync now runs the byte-for-byte Windows generator check for routed source or project
  changes, preventing the next session from inheriting stale file rows, LOC, symbols, or anchors.
- Updated build, verifier, agent, timeout, and maintenance guidance to the single Windows topology.

## 2026-07-22 — Linux repository extraction
- Moved the complete Linux/Avalonia source, tests, solution, supporting tools, and context layer to
  the new private `dhruuvsharma/DaxAlgo-Terminal-Linux` repository.
- Removed Linux-owned paths and obsolete Avalonia project items from this repository. Windows work
  has no Linux parity or mirror obligation; cross-repository coordination requires explicit scope.
- Reduced the context manager, generators, guides, recipes, modules, and product documentation to
  the Windows/WPF repository boundary; ADR-0013 records the decision.

## 2026-07-21 — backtest client graph repair
- Split host-side worker supervision into the WPF-free `TradingTerminal.Backtest.Client` project while
  preserving the existing client namespace/API and process-ownership behavior.
- Removed the forbidden `TradingTerminal.Infrastructure` to `TradingTerminal.Backtest.Protocol` edge;
  Backtest Studio and headless tests now reference the higher client layer directly.
- Added the client to both Windows edition filters and overlay solutions, regenerated routed Windows
  index/symbol context, and synchronized the direct/reverse dependency graph.

## 2026-07-21 — deterministic signed strategy bundle foundation
- Added the passive `.daxstrategy` v1 container, canonical UTF-8 manifest/content root, strict bounded
  archive and managed-payload validation, standard DSSE ECDSA P-256 publisher signing, and a portable
  `daxalgo-bundle` pack/sign/verify/inspect tool. Verification binds trusted keys to the manifest
  publisher and never loads strategy code.
- Added the explicit, parameter-aware `IStrategyEngineFactory` SDK contract and deterministic managed
  assembly graph so one WPF-free engine assembly is the live/backtest/optimizer implementation; optional
  Windows presentation stays separate. Advanced validation rejects host copies, native/mixed/R2R payloads, missing private
  dependencies, UI references in the engine closure, path aliases, executable resources, and tampering.
- Advanced the Windows SDK/tool/template chain to `0.2.0-alpha`; split the strategy template into a
  canonical Engine project plus optional WPF companion, added deterministic bundle packaging, stable
  publisher/capability metadata, and preserved the legacy `.daxplugin` adapter.
- Hardened legacy and bundle-tool output against normalized/junction/symlink/hard-link aliases, including
  non-overwriting final moves, and accepted PowerShell 5.1 UTF-8 BOM indexes. Extended CI for local-vNext/
  published package smoke and cross-root reproducibility.
- Verified 35 focused bundle tests, 871/871 shared headless tests, fresh headless/UI template builds and
  packages from local NuGets, and Basic/Intermediate/Professional Release builds. Runtime loading,
  immutable installation, marketplace attestations/revocation, and trust UX remain later slices.

## 2026-07-21 — isolated backtest worker and Studio workbench
- Added a versioned, WPF-free one-shot backtest worker plus a thin Infrastructure client. Jobs use
  immutable hashed inputs, bounded progress, atomic hashed reports, strict protocol/component checks,
  Windows Job Object ownership, cancellation/timeout/crash classification, and guarded cleanup.
- Kept the author-once invariant: the first worker slice executes the canonical managed engine
  assembly directly and pins its exact hash; it does not introduce a second strategy implementation.
  Exact-hash loading of arbitrary external strategy engine packages remains the SDK-vNext phase.
- Tightened canonical-engine causality and lifecycle: FIFO order callbacks, same-touch end-of-run
  liquidation/drain, disposal on all exits, and explicit rejection of unsupported execution options.
- Routed supported Backtest Studio single runs through the worker and redesigned the WPF surface as
  an MT5-inspired tester workbench with configuration rail, progress/cancel, metrics, report tabs,
  optimization/replay states, and truthful execution-target badges.
- Staged/published the isolated worker closure for Basic, Intermediate, and Pro. Verified 793/793
  headless tests, all three Windows edition builds, and a Basic publish containing the worker sidecar.

## 2026-07-20 — Windows tool-window lifetime and service-probe reliability
- Made the shared shell the single owner of standalone Charts, Order Book, Volume Footprint,
  Bookmap, Historical Correlation, and Live Correlation view-model disposal, removing deterministic
  close-time double disposal in Basic, Intermediate, and Professional compositions.
- Made chart reload cancellation and live-strategy start/stop/dispose ownership atomic and
  idempotent, including the close-while-starting case where a plug-in ignores cancellation.
- Replaced abandoned TCP reachability tasks with directly awaited, cancellation-aware connections;
  refused, timed-out, and cancelled login probes now complete as ordinary unavailable states.
- Made shared instrument-picker updates coalesce synchronous ComboBox re-entry and normalize equal
  broker rows before moving items, preventing recorder/tool launch `ArgumentOutOfRangeException`s.
- Added focused pending-reload, double-dispose, delayed-strategy-start, loopback, closed-port, and
  pre-cancelled probe regressions plus picker duplicate/re-entry coverage.
- Verified the complete shared Windows test project at 64/64 and an isolated Basic, Intermediate,
  and Professional launch/close/reopen matrix at 40/40 with no new crash-report filenames.

## 2026-07-20 — context integrity and Codex parity
- Repaired the active Windows layer: 28 projects / 911 `.cs`+XAML-family files; removed orphaned
  Windows strategy indexes/symbols, added Codegen/StrategyTool/StrategyComposer to the graph, and
  made the generator staged, orphan-pruning, date-stable, `.axaml`-aware, and `--check` capable.
- Added an isolated lazy Linux layer (`linux/`): 35 projects / 732 files with its own masters,
  dependency graph, group indexes, symbol shards, staged generator, and byte-for-byte check mode.
- Replaced stale Codex guidance with a concise `AGENTS.md`; added trusted-project Codex config for
  bounded agents/local memories and a schema adapter that runs the canonical repository hooks.
- Context prose now treats Windows first-party strategies as external runtime plugins and routes
  Linux, authoring/codegen, top-level tooling, and minified assets explicitly.
- Added a public-only `manage-context.ps1` with summary, structural check, and completion-grade
  deep check; added standalone task-memory guidance and simplified the mandatory protocol.
- Removed dead strategy/AI/CLI agents, duplicate inactive Codex hooks, and obsolete Windows strategy
  module docs; refreshed surviving agents, skills, recipes, and shell/module routes to current
  grouped paths and the external SDK-plugin workflow.
- Optimized the Windows generator without changing output: `--check` fell from about 308 seconds
  to 23.5 seconds by batching symbol and index extraction.
- Replaced volatile module LOC/line anchors with durable routing and compressed the Windows symbol
  and dependency masters; detailed generated shards remain lazy and authoritative for navigation.

## 2026-07-18 — #29: Vibe Quant rebuilt as an agent workspace (#28 round 3)
- **The shared Windows authoring window is new** (`StrategyAuthoringView` in both public edition
  shells): session rail
  (cards + delete-on-hover + provider health + collapse) | agent canvas (document transcript,
  hero empty state with suggestion briefs, cockpit composer with model/build/reasoning pill
  flyouts, shimmer status + compact clock while generating) | workbench (ID/NAME + gradient
  **Compile & Register** + DRAFT/REGISTERED chip; underline tabs Code/Parameters/Activity).
  Patterns researched from Cursor 2/3, the Codex app, Claude Code desktop, claude.ai, Kimi —
  plan artifact linked from issue #29.
- **Transcript kinds**: `AuthoringMessage.Kind` (string, duck-typed contract with the shared
  templates) — User/Assistant/Note/**Tool** (compile · self-review · backtest smoke · provider
  error · registered, expandable output)/**Plan** (per-turn live checklist card holding that
  turn's `BuildTask` instances)/**Files** (per-file `+N −M` chips → click focuses the workbench
  editor). Persistence: `AuthoringChatEntry` +`Kind/State/Detail`, snapshot +`Registered` —
  old JSON still loads. Assistant code fences render as a marker (`StripCodeFencesConverter`);
  the code lives in the workbench.
- **Review gate (P4)**: `Compile & Register` → compile → **review overlay** (per-file line
  diffs vs last registered content via new bespoke `LineDiff` LCS in Settings — no package —
  + consent line); `ConfirmRegister` is the only path to registration.
- **VibeQuantStyles.xaml** (TradingTerminal.UI, merged by both public edition shells): the ONE
  sanctioned soft
  zone — radii 7/10/14, glass, gradient budget (mark/CTA/Send), composer ring + shimmer gated
  on IsGenerating/IsVisible at 24fps (idle = fully static). Duck-typed by design: Settings is
  WPF-free, so templates bind by name and trigger on the Kind/State string constants.
- **CodeEditor** (AvalonEdit 6.3.0.90, MIT, added to TradingTerminal.UI → note: rides the SDK
  contract chain): bindable `Code` DP, line numbers, embedded theme-neutral C# .xshd.
- **VM additions (shared, portable)**: HasConversation, suggestion briefs, auto-derive id+name
  from the first brief (only while both are untouched defaults), WorkingVerb/StepText/
  ElapsedCompact, rail collapse, WorkbenchTab + FocusFile, IsRegistered, review-gate state.
- Tests: +12 unit tests (`VibeQuantTranscriptTests` — LineDiff, chip pack/unpack, kind-string
  contract, plan snapshot glyphs) → **82/82** public WPF. Both public edition solutions green.

## 2026-07-17 (later) — #28 round 2: sharp geometry, real tabs, themed menus, emoji life
- **GEOMETRY RULE (Dhruv's call): everything is SQUARE** — scripted sweep zeroed **200 corner
  radii** (178 attribute-form + 22 setter-form) across 50+ XAML files in both repos; the **only
  rounded element left is the Vibe Quant hero button** (AiStyles radii 24/25). Badges, pills,
  cards, popups, tooltips, inputs: all sharp. Comments updated to state the rule.
- **Menus finally follow the theme.** Root cause: MahApps' menu templates read
  `MahApps.Brushes.SubMenu/ContextMenu/MenuItem.*` + `WindowTitle` keys our palettes never
  defined → dropdowns stayed MahApps-native black. Key names **verified empirically** (scratch
  dumper over MahApps 2.4.10 Dark.Blue.xaml + Controls.xaml), then pinned in **all 6 palettes**
  (submenu/context bg+border, selection fill, top-level pressed, disabled fg, separator,
  window title active/inactive, DataGrid selection family).
- **Tabs look like tabs**: implicit `TabControl`/`TabItem` in Dark.xaml — TV-style text tabs,
  2px accent underline + soft accent wash on selected, hover hint, hairline between tab row and
  content. (No TabStripPlacement≠Top usage anywhere — verified.)
- **New implicit styles kill the last native-grey chrome**: GroupBox (sharp hairline frame +
  small-caps header), GridViewColumnHeader (flat, hover, keeps PART_HeaderGripper resize),
  DataGrid + DataGridColumnHeader (flat surface, horizontal hairlines), ContextMenu (Font.Base).
- **Emoji iconography ×3 shells** — `MenuItem Icon="…"` across File/View/Tools/Charts/Data/
  Settings/Help + the strategy-card context menu (🔄🗃️🚪📋🎨🖌️📊🌡️🔗📡⌨️🧩📈📚👣🗺️🧊🧪📦🕘📤🔔❤️ℹ️▶️⏱️✏️);
  AI features keep the gradient AI chip as their icon.
- **Basic + Intermediate caught up with Pro** (missed in round 1): Vibe Quant FAB →
  `Ai.HeroButton`, Strategy Studio menu header → gradient ink + violet glow, AI chips on
  Vibe Code / Vibe Quant / AI providers.
- Builds green ×2 repos; 70/70 WPF + 8/8 Pro tests. Visual QA in DevSim pending.

## 2026-07-17 — #28 AI-Native design system: DaxAlgo Dark default + the AI glass language
- **Two new palettes**: `Themes/TvDark.xaml` (**DaxAlgo Dark** — new default builtin: `#131722`
  canvas, `#1E222D` surfaces, `#2962FF` accent, `#089981`/`#F23645` P&L, sans `Font.Base`) and
  `Themes/TvLight.xaml` (light counterpart; flips MahApps native surfaces like GreekLight).
  Fresh installs default to daxalgo-dark; saved theme prefs win.
- **Every palette gained the same additions** (key superset intact): `Font.Base` (UI chrome font —
  amber/mono stay Consolas on purpose, Greek+TV go Segoe UI Variable), `Font.Data` (numeric
  columns), and the **`Ai.*` family** (`Ai.Accent/Glow/Text` +.Color/.Brush pairs, `Ai.Soft`,
  `Gradient.Ai` violet→cyan, `Gradient.AiGlass` translucent wash). Amber also gained
  `MahApps.*.IdealForeground` (black). ThemeManager groups `Ai.*` as "AI (glass & gradients)".
- **Structural consistency pass** (`Dark.xaml`/`Components.xaml`/`StrategyShellStyles.xaml`):
  one radius scale (6 controls / 10 cards / 8 tooltips — `Strategy.SetupCard` was square),
  `Font.Base` in all chrome styles, `Font.Data` replaces hardcoded Consolas, `Surface.Hover`
  replaces hardcoded `#1C1C1C`, neutral minimal default Button (accent on interaction only,
  wash 0.10) + new keyed `Button.Primary` CTA, softer shadows.
- **NEW `Themes/AiStyles.xaml`** — the AI-native visual language (theme-agnostic): `Ai.Badge`/
  `Ai.BadgeText`, drop-in `Ai.MenuIconChip` (x:Shared=False), `Ai.Pill`, `Ai.TextAccent`
  (gradient ink), `Ai.GlassCard` (static glow) and `Ai.GlassPanel`/`Ai.HeroButton` with a
  **sweeping animated gradient ring** (named RotateTransform in template, 24 fps, fixed brand
  hues — DynamicResource stops in animated freezables are unreliable). Merged in App.xaml ×3.
- **Shells ×3**: API-meter chrome tokenized (`Gradient.Elevated`/`Gradient.Header` replace inline
  hexes), research-paper tag → `Ai.Pill`+`Ai.Text.Brush` (was `#4527A0`), F1 HELP →
  IdealForeground. Pro additionally: Vibe Quant FAB → `Ai.HeroButton`, Strategy Studio menu
  header → `Ai.TextAccent`+violet glow (white-blob DropShadow removed), AI menu chips on
  Vibe Code / Vibe Quant / Paper Lab / Research settings / AI providers, AI-NATIVE brand chip.
- Tests: 70/70 WPF + 8/8 Pro (one stale Pro test re-pinned: the Chat|Code|Parameters tabs died
  in the 0b81ad2 Vibe-Quant redesign; now pins splitters + ChatScroll). Both slnx builds green.
  Visual QA in DevSim pending. Tracker: **#28** (linked from #4).

## 2026-07-15 — #26 the default-UI composer: a viewless authored strategy gets a composed live window
- **The pack contract changed: the model writes THREE files (kernel + descriptor + view-model), not
  four.** A view is optional and discouraged — when the author writes none, the host composes the live
  window from the descriptor's `DataRequirement`: `Bars` → `ChartsPanel`, `Depth` → `OrderBookPanel`,
  `TradeTape` → `VolumeFootprintPanel` (all in their `Embedded` ML-off presets), `L1`-only → a quote
  card; plus the shared setup hero, chrome bar, Start/Stop/arm strip and a signal-feed grid.
- **New project `src/windows/UI/TradingTerminal.StrategyComposer`** — `ComposedStrategyView` (the
  window body; owns the panel VMs, pushes the strategy's instrument into them on `IsConfigured`/change,
  relays pause, disposes them on window close) + `AuthoredStrategyViewComposer` +
  `AddStrategyViewComposer()` (registered ×3 shells). Contract:
  `Core/Strategies/Authoring/IAuthoredStrategyViewComposer` (UI-free so the SDK can name it).
- **Panel VMs got explicit embed options** (`OrderBookEmbedOptions` / `VolumeFootprintEmbedOptions` /
  `ChartsEmbedOptions`, passed via `ActivatorUtilities`): the pinned instrument and `MlEnabled` land
  BEFORE the ctor's first `Restart()`, so an embedded panel never ghost-subscribes the standalone
  tool's persisted instrument (SPY) and never trains a forecaster; embedded VMs also never write
  `LastInstrumentStore`. The standalone windows resolve without options — behaviour unchanged.
- **Both catalog paths compose.** In-session: `AuthoredStrategyInstaller` (optional
  `IAuthoredStrategyViewComposer` ctor arg) registers the card when descriptor+VM exist and either a
  view or a composer does. Restart: the SDK's `AuthoredPluginBootstrap` now registers a
  `StrategyFactoryRegistration` whose `ViewFactory` resolves the composer at open time — a composed
  strategy survives restarts like a hand-written one. `AuthoredStrategyAssembly.CanComposeLiveWindow`
  is the new gate; `MissingForCatalog` no longer lists a view.
- Pack regenerated (`build/gen-ai-context.ps1` → `sdk/ai-context/daxalgo-strategy-context.md`); the
  template's CLAUDE.md/AGENTS.md (contract b) unchanged. `docs/ai-strategy-builder.md` gained "The
  live window — composed by default".
- Tests: `AuthoredStrategyComposerTests` (headless, 6 — compiler report / installer with+without
  composer / bootstrap with+without composer, end-to-end through a real `StrategyFactory.Create`) +
  `ComposedStrategyViewTests` (WPF, 5 — flag→panel mapping, Embedded presets + `MlEnabled=false`,
  L1 quote card, instrument push incl. broker pinning, idempotent dispose that detaches). One old
  codegen assertion updated to the new contract. **763 headless + 66 WPF green.**

## 2026-07-14 (later+4) — #26 the three chart tools are now embeddable, feature-gated panels
- **`OrderBookPanel` / `VolumeFootprintPanel` / `ChartsPanel`** (Charts group) — every window that an
  authored strategy might want to *show* is now a `UserControl`; `OrderBookWindow` / `VolumeFootprintWindow`
  / `ChartsWindow` are thin frames around them (`<local:XPanel Features="{x:Static X.Full}"/>` + disposing
  the VM on close). Nothing about the tools changed: the standalone windows still get every feature.
- **`XPanelFeatures` records** — build-time gates, not user toggles. Off is not "hidden": the section is
  collapsed **and** the view-model toggle behind it is forced off, so the work is skipped. `MlEnabled` on
  both ML-bearing VMs is the sharpest one: false means the predictor is **never created** — no warm-start
  replay over stored tape, no per-tick/per-bar training, no inference. `Embedded` / `LadderOnly` /
  `ChartOnly` presets are what strategy windows will be handed; a test pins that none of them train a
  model or keep a toolbar (a panel toolbar would let the user point it at a different symbol than the
  strategy trades).
- **`Directory.Build.props` (both repos): `AssemblyName`/`RootNamespace` are no longer set for WPF's
  `_wpftmp` project.** Deriving them from `$(MSBuildProjectName)` renamed the temporary pass-2 assembly
  WPF builds to resolve same-assembly XAML types — so *any* window hosting a UserControl from its own
  project died with `MC3074: the tag does not exist in XML namespace`, pointing at nothing. This had never
  bitten because no XAML here had ever referenced a local type.
- **`ChartsWindow` now disposes its view-model** (it never did — every open/close leaked the hub
  subscriptions; the other two windows always disposed theirs).
- Tests: `ChartPanelTests` realizes all three panels against the real theme dictionaries (XAML +
  `RelativeSource AncestorType` gate bindings only break at runtime). `WpfTestApp` gives the
  Application-dependent tests one owned STA thread — `[WpfFact]` gives each test a *fresh* thread, and
  WPF's one-Application-per-AppDomain rule plus `ShutdownMode.OnLastWindowClose` made failures land on
  whichever test happened to run second.
- 757 headless + 61 WPF + 13 Pro green. Next: the DataRequirement-composed default UI, which now has
  panels to compose.

## 2026-07-14 (later+3) — #26 domain skill packs (on-demand, session-stable)
- **`sdk/ai-context/skills/*.md`** — five hand-written domain packs, embedded in DaxAlgo.Codegen:
  `order-flow` (footprint/imbalance/VPOC/depth, trade signing, the pitfalls), `quant-math` (stable
  estimator forms, OU half-life, VPIN/Kyle), `risk-and-exits`, `live-window` (base-VM surface +
  memory-safety), `instruments-and-data` (Contract, DataRequirement, which feeds actually exist).
- **`StrategySkillLibrary`** parses `---` front matter (id/name/triggers) and scores a brief by distinct
  trigger hits; capped at 3 packs / 12k chars. An EMA-cross brief pulls `quant-math` and NOT `order-flow`
  — it doesn't pay for microstructure it never uses.
- **Chosen ONCE per session, never per turn.** The system prompt is the cached prefix of every request in
  a thread; re-selecting mid-conversation would change those bytes and throw the cache away each turn,
  costing far more than any pack saves. A resumed session re-resolves from the restored user turns, so it
  lands on the same prompt. A test pins byte-identity across turns.
- Base pack slimmed (the quant cheatsheet + memory-safety notes moved into packs where they can be deep
  rather than squeezed) and gained a **scope guard**: this window builds DaxAlgo strategies and nothing
  else.
- 757 headless + 57 WPF + 13 Pro green. Next: the DataRequirement-composed default UI.

## 2026-07-14 (later+2) — #26 token economics: the files are STATE, not conversation
- **The bug in the shape of the prompt.** Every rewrite emits the whole file set again, so the raw thread
  carried N superseded copies of the code and re-sent all of them on every turn — cost grew with the
  square of the work. (Context was never lost; it was paid for repeatedly.)
- **`StrategyBuildSession.WireMessages()`**: history keeps what the model *said* (`StripCode` replaces each
  fenced block with `[code omitted: wrote Kernel.cs (120 lines)…]`), and exactly **one** copy of the code
  — the editor's current contents — rides along with the newest turn. Prompt size is now roughly flat:
  pack + prose + current code. `WithUserEdits` in the VM is gone; the editor is simply the truth
  (`SyncEditedFiles` before each send).
- **Prompt caching (Anthropic)**: `system` and `messages` are now content-block arrays; two
  `cache_control: ephemeral` breakpoints — the byte-identical SDK pack, and the last message (making this
  turn's prompt the cached prefix for the next). `CodegenUsage` gained `CachedInputTokens`, surfaced in the
  header (`12,000 in (9,500 cached)`) — a session where it stays at zero is one paying full price.
- Measured on a 3-turn / 6-generation session with a 4-file plugin: **~129k → ~53k input tokens (-59%)**,
  and linear rather than quadratic from here.
- 754 headless + 57 WPF + 13 Pro green. Next: domain skill packs, then the DataRequirement-composed
  default UI (needs the four chart windows extracted into embeddable UserControls).

## 2026-07-14 (later) — #26 chat history persists (and the model's memory with it)
- **`AuthoringSessionStore`** (Settings/Authoring) — one JSON per strategy id under
  `%LocalAppData%\DaxAlgo Terminal\authoring\`. Saves the chat, **the model's own thread**, the files, the
  provider/model/effort, and the token totals. Written after every turn, every Compile & Register, and on
  pane close (hand-edits aren't saved per keystroke).
- **The thread is the point.** Restoring only the bubbles would give a transcript the model has never
  seen — "now tighten the stop" would arrive with no idea what the stop is. `StrategyBuildSession` /
  orchestrator / `IAiStrategyBuilder.StartSession` gained `history` + `priorUsage`, so a resumed session
  replays the whole conversation (including the compiler's auto-fix prompts) and the token counter
  continues rather than restarting.
- Pane: a **Chat picker** (saved sessions, newest first, with age) + Delete chat (deletes the
  conversation, never the registered strategy). The builder opens on the last session you were in.
  `New chat` banks the outgoing conversation first — it can never cost you one.
- The strategy id is user input and becomes a file name: every path separator is scrubbed, and a test
  pins containment (not a spelling) so an id can't escape the folder.
- 750 headless + 57 WPF + 13 Pro green.

## 2026-07-14 — #26 fix: an authored strategy had NO plugin entry point (so it died on restart)
- **The restart bug.** `PluginLoader.RegisterFromAssembly` requires a public `IStrategyPlugin`. The
  authored DLL had none, so on the next start the loader found it, rejected it, and the Plugin Manager
  said *failed to load* — and the catalog card vanished with it. Phase 2 persisted a DLL that was not,
  in fact, a plugin.
- **Fix:** `RoslynStrategyCompiler` now emits a generated `Plugin.g.cs` into every authored assembly — a
  4-line `IStrategyPlugin` that calls the new **`DaxAlgo.Sdk.AuthoredPluginBootstrap`**. The bootstrap
  discovers kernel / descriptor / view-model / view **by shape** (`AuthoredStrategyTypes.DiscoverIn`) and
  registers exactly what a hand-written `AddXxxStrategy()` would: `BacktestStrategyOption`,
  `ITradingStrategy` (**by type**, so the loader's ImplementationType attribution still badges it DEV),
  and the VM+view behind a `StrategyFactoryRegistration`. One discovery implementation now serves both
  the in-app installer and the loader, so the pane and the restart can't disagree.
- **The model wasn't writing the UI.** The pack framed the descriptor/VM/view as an optional extra; it now
  says **write all four files** unless the user explicitly asks for a backtest kernel only, and tells the
  model NOT to write an `IStrategyPlugin` (the host generates it — a second one would be ambiguous). The
  agent-CLI prompt tail says the same.
- Chat history is still **in-memory only** (session dies with the app) — known gap, not yet addressed.

## 2026-07-13 (later+2) — #26 AI Strategy Builder v2, phase 3: streaming, live tokens, reverse questions
- **`IStrategyCodegenClient.StreamAsync` → `IAsyncEnumerable<CodegenEvent>`** (TextDelta / UsageUpdate /
  Completed). It is a **default interface member** that aggregates `GenerateAsync`, so a provider that
  can't stream (Fake, Codex) needs zero code and no caller ever branches on whether streaming exists.
- **One parser, two transports.** Claude Code's `--output-format stream-json --include-partial-messages`
  emits `{"type":"stream_event","event":{…}}` wrapping *the very same* Anthropic SSE events — verified by
  running the CLI, not assumed — so `AnthropicEventAccumulator` reads both. Anthropic SSE + OpenAI
  `stream:true` (+ `stream_options.include_usage`) round it out.
- **Usage counts what was actually processed**: `input_tokens + cache_creation + cache_read`. Reporting
  bare `input_tokens` showed a 2-token prompt for a 15k-token context pack.
- **The chat grows token by token** (`AuthoringMessage.Text` was already `[ObservableProperty]` for
  exactly this); token counters tick live, and settle to the session's authoritative total at turn end
  (a turn can be several generations — the auto-fix retries).
- **Reverse questions**: a no-code reply was already a `Question` turn; the pane now surfaces it as an
  unmissable banner + `AwaitingAnswer`, so an under-specified brief gets asked about instead of guessed at.
- Iterators can't yield from a catch — both HTTP clients send through a `TrySendAsync` that classifies the
  failure first. 748 headless + 57 WPF + 10 Pro green. **#26 phases 1–3 done.**

## 2026-07-13 (later) — #26 AI Strategy Builder v2, phase 2: an authored strategy is a real plugin
- **The catalog is mutable now.** `IStrategyFactory` gained `Register(strategy, registration)` +
  `Changed`; `StrategyFactory` (UI.Core) backs it with a locked list and replaces by id (a regenerate
  updates the card, it doesn't duplicate it). The Pro shell's Strategies pane subscribes and shows the
  card immediately — no restart. **Blast: Core seam + UI.Core impl + Pro shell.**
- **`AuthoredStrategyInstaller`** (Infrastructure/Strategies/Authoring) is what "Compile & Register" now
  means: backtest registry + catalog card + a real plugin written to `<pluginsRoot>/<id>/` (dll +
  plugin.json, SdkInfo.Version) so the ordinary `PluginLoader` picks it up on the next start with zero
  special-casing + a Plugin Manager row (`PluginHostContext.AddAuthored` / `AuthoredThisSession`, rows ×3
  shells). The gate is unchanged: the image already went through the same `PluginPolicyScanner` the
  plugin loader applies, and the user clicked the button.
- **The AI writes the whole plugin.** `RoslynStrategyCompiler` reflects the emitted image into
  `AuthoredStrategyAssembly` (kernel + optional ITradingStrategy descriptor + live VM + view); VM/view are
  matched by base-type NAME (`TradingTerminal.UI.LiveSignalStrategyViewModelBase`,
  `System.Windows.Controls.UserControl`) because Infrastructure must not reference UI.Core. Live-window
  global usings are injected **only when UI.Core is in the reference set** — a headless host (backtest
  CLI) has no WPF, and a global using of a missing namespace would fail every compile there.
- Kernel-only ⇒ backtest-only, and the status line names exactly which of the three files is missing
  rather than putting a card on the pane that throws when clicked. A view is code-built C# (Roslyn cannot
  compile XAML); the pack now carries the exact descriptor / VM-ctor / view shapes.
- Assembly is loaded from the byte[], never the file it writes — the DLL is never locked, so regenerating
  overwrites cleanly.
- Tests: 743 headless + 57 WPF + 10 Pro. The Pro pair is the real end-to-end — a 4-file plugin compiled
  against the host's own UI assemblies, registered, opened via `catalog.Create()`, persisted, listed.

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
- **Model ids + reasoning effort (same-day fix).** The picker offered the Claude Code CLI's *aliases*
  (`opus`/`sonnet`/`haiku`) instead of model ids, and had no effort control. `AiModelCatalog` now lists
  real ids (`claude-opus-4-8`, `claude-sonnet-5`, `claude-opus-4-7`, `claude-haiku-4-5`,
  `claude-fable-5`) for both `anthropic` and `claude-cli` (Claude Code's `--model` takes either), and
  ships nothing for providers whose ids we'd have to guess — they use the live `/models` fetch.
  New `CodegenEffort` (Default/Low/Medium/High/XHigh/Max) reaches each provider the way it actually
  takes it: Anthropic `output_config.effort` + `thinking:{type:adaptive}`, OpenAI-compatible
  `reasoning_effort` (xhigh/max clamp to high), Claude Code `--effort`, Codex nothing (it has no such
  flag). **Default sends no parameter at all** — Haiku 4.5 and older reject both, so wire-silence is
  what keeps them usable. Persisted per provider in `ai-codegen.json`.
- Phases 2 (plugin emit + hot catalog/Plugin-Manager registration) and 3 (streaming + reverse questions)
  follow. 741 headless + 8 Pro tests green.

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
