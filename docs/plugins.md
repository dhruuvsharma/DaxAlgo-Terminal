# Strategy plugins

> Last updated: 2026-06-29

DaxAlgo Terminal is **open-core**: the terminal is free and open source, and strategies can ship as
**separate plugins** — built and distributed independently, then loaded at runtime from a folder
(MT5-EA style). This page is for two audiences:

- **Users** who want to install a strategy plugin into the app.
- **Developers** who want to build, test, and publish a strategy plugin.

A plugin is a normal .NET assembly that references the **DaxAlgo SDK** and exposes one
`IStrategyPlugin`. The host discovers it, verifies it, and registers its strategy through exactly the
same dependency-injection seam the built-in strategies use — no host recompile.

> **In plain terms.** A *plugin* is an add-on strategy that someone else built, which you drop into
> the app — like installing an Expert Advisor in MetaTrader, or an extension in your browser. You
> don't rebuild anything or read any code: you point the app at the plugin file, it checks the plugin
> is genuine, and the new strategy appears in your catalog next to the built-in ones. **Open-core**
> just means the terminal itself is free and open source, while strategies can be shipped and sold
> separately as plugins.

> **Windows only (for now).** The plugin system and the DaxAlgo SDK live in the Windows/WPF build.
> The Linux/Avalonia tree doesn't ship the SDK yet, so plugins are a Windows feature today. (The rest
> of the app — the 12 built-in strategies, tools, brokers — runs on both.)

---

## For users: installing a plugin

Open **Plugins → Manage strategy plugins…**. The window shows:

- the plugins folder (`<app>/plugins`),
- the trust policy in force (see [Trust & signing](#trust--signing)),
- the plugins currently loaded.

> 🖼️ **Screenshot:** `images/plugins-manager.png` — the Plugin Manager: installed-plugins list, the
> trust policy badge, and Install-from-file.

**Install plugin…** lets you pick a plugin's main `.dll`; the app validates it (manifest + signature,
without running its code) and copies the package into the plugins folder. You can also click **Open
plugins folder** and drop a plugin folder in by hand.

> Plugins are loaded at startup, so **restart the app** to activate a newly-installed plugin. It then
> appears in the strategy catalog like any built-in strategy.

The plugins folder layout the loader expects:

```
<app>/plugins/
  MyStrategy/
    MyStrategy.dll        # the assembly that contains the IStrategyPlugin (folder name == dll name)
    plugin.json           # optional manifest (recommended)
    MyStrategy.pdb        # optional, for debugging
```

---

## For developers: building a plugin

### 1. Start from the sample

The fastest start is to copy [`samples/DaxAlgo.SamplePlugin`](../samples/DaxAlgo.SamplePlugin) — a
minimal, headless, backtest-only plugin that already wires the whole contract.

### 2. Reference the SDK

A plugin references the SDK packages, **not** the host's internal projects:

| Package | What it gives you |
|---|---|
| `DaxAlgo.Sdk` | The headless contract — `ITradingStrategy`, `IStrategyKernel` / `IBacktestStrategy`, `BacktestStrategyOption`, the parameter schema, market-data DTOs, `Core.Quant` math, and the `IStrategyPlugin` / `IPluginRegistrar` plugin seams. |
| `DaxAlgo.Sdk.Wpf` | Adds the WPF UI base for a strategy *window* — `LiveSignalStrategyViewModelBase`, `StrategyWindowBase`, the param controls, the Activity Log sink. Only needed if your plugin ships a custom WPF window. |

In-repo these are project references; published they are NuGet packages. A **headless / backtest-only**
plugin needs only `DaxAlgo.Sdk`. A plugin with a **live strategy window** needs `DaxAlgo.Sdk.Wpf`.

### 3. Implement `IStrategyPlugin`

The single entry point. Its `Register` body is identical to a first-party `AddXxxStrategy()` — register
your `ITradingStrategy` descriptor, your `BacktestStrategyOption`, and (for a UI plugin) the view + view-model
+ `StrategyFactoryRegistration`.

```csharp
using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

public sealed class MyPlugin : IStrategyPlugin
{
    public string Name => "My Strategy";
    public string TargetSdkVersion => SdkInfo.Version;   // the SDK you built against

    public void Register(IPluginRegistrar registrar)
    {
        // Catalog metadata (the Strategies pane).
        registrar.Services.AddSingleton<ITradingStrategy, MyStrategy>();

        // Backtestable engine entry — aggregated into the same registry the host uses, so it shows
        // up in Backtest Studio and the CLI with no host change.
        registrar.Services.AddSingleton(new BacktestStrategyOption(
            Id: "my.strategy",
            DisplayName: "My Strategy",
            Build: contract => new MyBacktestStrategy(contract)));

        // For a live window plugin, also (on the WPF leg):
        //   registrar.Services.AddTransient<MyStrategyViewModel>();
        //   registrar.Services.AddTransient<MyStrategyWindow>();
        //   registrar.Services.AddSingleton(new StrategyFactoryRegistration(
        //       StrategyId: "my.strategy",
        //       ViewFactory: sp => sp.GetRequiredService<MyStrategyWindow>(),
        //       ViewModelFactory: sp => sp.GetRequiredService<MyStrategyViewModel>()));
    }
}
```

Your strategy's engine (`IBacktestStrategy`) and any domain types live **inside the plugin** — it owns
its full vertical and depends only on the SDK. (See `SigmaIcFlow` for a real example: its
`ApexScalperStrategy` engine and Apex types live in the plugin's `Engine/` folder.)

### 4. Add a manifest (`plugin.json`)

Drop a `plugin.json` next to your assembly (set it to copy to output). The host reads it **before
loading any code** — for the SDK-version check, and so a curated host can require declared provenance.

```json
{
  "id": "my.strategy",
  "name": "My Strategy",
  "version": "1.0.0",
  "targetSdkVersion": "0.1.0-alpha",
  "publisher": "Your Name",
  "permissions": ["fileIo"]
}
```

`permissions` **declares** the Warn-level capabilities your plugin uses (`fileIo`, `network`,
`environment` — see the policy scan below). A declared capability is *disclosed* in the Plugin Manager
instead of being flagged. Block-level capabilities can never be declared away.

---

## Testing on your machine

The developer (open-core) build runs a **permissive** trust policy: unsigned local plugins load. So the
loop is just *build → install → restart*:

1. **Build** your plugin (`dotnet build`). Note the output folder, e.g.
   `bin/Debug/net9.0-windows7.0/MyStrategy.dll`.
2. **Install** it one of two ways:
   - In the app: **Plugins → Manage strategy plugins… → Install plugin…**, pick `MyStrategy.dll`.
   - By hand: copy the folder to `<app>/plugins/MyStrategy/` (folder name must match the dll name).
3. **Restart** the app. Your strategy appears in the catalog; the **Activity Log** logs
   `Loaded strategy plugin My Strategy (DaxAlgo.Sdk …)`, or a clear error if it was rejected.

For automated/headless testing, the `daxalgo-backtest` CLI loads plugins from its own `plugins/` folder
too — drop the package next to the exe and run `daxalgo-backtest run --strategy my.strategy …`.

A SigmaIcFlow plugin package is produced automatically by the app build at
`src/windows/Shell/TradingTerminal.App/bin/<config>/<tfm>/plugins/TradingTerminal.Strategies.SigmaIcFlow/`
— a working reference package to inspect.

---

## Trust & signing

An in-process strategy gets the user's broker session and machine and **cannot be sandboxed** (it needs
low-latency market-data access). So distribution is **curated + code-signed**, not open submission.

| Policy | Behaviour | Used by |
|---|---|---|
| **Curated** | A plugin loads if it is one the build **pinned by hash**, *or* is signed by a publisher whose certificate **thumbprint the host pins**, *or* you **explicitly consent** to it. | **The shipped default.** |
| **Permissive** | Loads any plugin, unsigned included; signatures aren't inspected (integrity and the IL scan still are). | The `Dev*` launch profiles, so plugin authors aren't re-prompted on every rebuild. |

Set it in the `Plugins` section of `appsettings.json`:

```jsonc
"Plugins": {
  "TrustPolicy": "Curated",         // or "Permissive"
  "TrustedThumbprints": [],         // pinned publisher certificate thumbprints
  "ScanMode": "Enforce"             // Enforce | WarnOnly | Off — the IL policy scan below
}
```

Thumbprint *pinning* — not merely "any valid signature" — is the gate: only assemblies signed by a known
publisher load. The signature is verified with `WinVerifyTrust` (so a tampered DLL fails), and every
verification failure path **rejects** the plugin (never wrongly accepts it).

### How the app trusts its own plugins (hash pinning)

The first-party strategies ship as plugins like any other, and they are **not code-signed** — so under
Curated they would be rejected along with everything else, leaving you with an empty strategy catalog.
Instead, the build records the sha256 of every assembly it staged into
`plugins/plugins-trusted.json`, and Curated accepts a plugin whose folder **hashes exactly to what the
build produced**. No certificate involved.

That same file is the **integrity baseline**, and it is checked in *every* trust mode — Permissive
included:

- an assembly modified, swapped, **added**, or removed inside a shipped plugin folder ⇒ the plugin is
  quarantined as **"Blocked — file changed on disk"**, rather than the app running rewritten code;
- for plugins *you* installed, the host records the assembly's hash at install time and re-checks it on
  every start — so a third-party plugin that changes behind your back is caught the same way.

### Unsigned plugins: you decide

A plugin that is neither ours-by-hash nor signed by a pinned publisher is **not silently loaded, and
not silently dropped**. The host shows you what it is — name, declared publisher, file, its sha256, and
the capabilities the IL scan found — states plainly that an in-process plugin can read your broker
session and credentials, and asks. Nothing is trusted merely because there was nobody to ask: in a
headless host (the backtest CLI, CI) the answer is always **no**.

Your answer is remembered against the **assembly's sha256**, so:

- you're asked once per plugin build, not once per start;
- when the plugin updates, its hash changes and it **asks again** — a new build inherits nothing from
  the trust its predecessor was given;
- a consented plugin wears a permanent **DEV (unsigned)** badge in the Plugin Manager. It runs because
  you said so, and the app keeps saying that out loud.

A plugin the scan **Blocks** is never offered for consent — you cannot click through P/Invoke or
`Process.Start`.

### Revocation

`plugins/revoked.json` is a local kill-list, checked on every load before any plugin code runs:

```json
{ "revoked": [ { "sha256": "9F86D0…", "reason": "exfiltrates credentials" },
               { "id": "some.plugin", "reason": "publisher compromised" } ] }
```

A build found to be malicious *after* people installed it is switched off by hash (that one build) or
by plugin id (every build of it) — the plugin is quarantined at the next start. The marketplace feed's
revocation list will sync into this file.

### The registration seam is add-only

Trust decides *whether* a plugin loads. A second layer constrains what a loaded plugin may do to the
host's dependency-injection container, because Microsoft.Extensions.DependencyInjection resolves the
**last** registration of a service type: a plugin handed the raw container could re-register
`ICredentialStore`, `IBrokerSelector`, or `IMarketDataStore` and silently intercept your broker
session, credentials, and market data.

`IPluginRegistrar.Services` is therefore **not** the host container — it's a guarded view:

- a plugin may register **new** service types of its own (its strategy, view-models, windows), plus
  additional `ITradingStrategy` / `BacktestStrategyOption` / `StrategyFactoryRegistration` entries
  (the seams the host deliberately resolves as collections);
- registering, replacing, or removing a service the **host already provides** is refused — the plugin
  is quarantined, shows "Blocked — unsafe registration" in the Plugin Manager, and registers *nothing*
  (its registrations are staged and only committed if `Register` returns cleanly, so it can't get
  half-applied);
- `TryAdd*()` keeps its normal no-op semantics, so defensive `TryAddSingleton` calls still work.

Every plugin's registrations are logged with the plugin's name, so any service in the running app is
attributable to whoever registered it.

### Static policy scan

Before a plugin is loaded — while it is still just bytes on disk — the host reads its IL (and its
bundled dependencies') and looks for capabilities a trading strategy has no business having:

| Verdict | Capabilities | What happens |
|---|---|---|
| **Block** | P/Invoke (`DllImport`), starting processes, the Windows registry, `Reflection.Emit`, loading assemblies | The plugin **does not load** and is quarantined; the Plugin Manager says "Blocked — unsafe code". Install is refused too. |
| **Warn** | File I/O, network I/O, writing environment variables | Loads. The capability is shown in the Plugin Manager ("uses fileIo"), so nothing is hidden from the user. |

A plugin can **declare** its Warn-level capabilities in `plugin.json` (`"permissions": ["fileIo"]`) —
declared capabilities are disclosed rather than flagged. **Block-level capabilities can never be
self-granted**; an unreviewed plugin cannot wave itself through, only human review (curation) can.
Set `Plugins:ScanMode` to `WarnOnly` while debugging a plugin the scan blocks, or `Off` to skip it.

**Be clear about what these two layers are.** An in-process .NET plugin runs with full process
privileges — it can reflect straight past DI, and a determined attacker can hide a payload behind
reflection over strings that no static scan will see. The guard and the scan are tripwires against
lazy or accidental abuse, and a disclosure surface for you. They are **not** a sandbox.
**Curation and code signing remain the actual control.**

To **publish** to a curated channel:

1. **Version**: set `TargetSdkVersion` to the `DaxAlgo.Sdk` you built against. The host accepts a plugin
   whose major (post-1.0) — or exact major.minor (pre-1.0) — matches its own SDK; otherwise it's rejected.
2. **Sign**: Authenticode-sign your `.dll` with your code-signing certificate
   (`signtool sign /fd SHA256 /f cert.pfx MyStrategy.dll`).
3. **Submit** your signing-certificate thumbprint so it can be pinned, and ship the package
   (`MyStrategy/MyStrategy.dll` + `plugin.json`).

---

## Notes & limits

- **Restart to activate.** Plugins load at startup; hot-reload is not implemented.
- **Cross-load-context.** Each plugin loads in its own collectible `AssemblyLoadContext`; the host's
  contract + shared WPF assemblies (`DaxAlgo.Sdk*`, `TradingTerminal.*`, `Microsoft.Extensions.*`,
  `CommunityToolkit.*`, `MahApps.*`, `ScottPlot*`) are shared with the default context so a plugin's
  types — including its `MetroWindow` — have the same identity as the host's.
- **Data/signals only.** Like the rest of the terminal, plugins do not place live orders.

See also: [contributing.md](contributing.md) (the in-tree strategy seam), [architecture.md](architecture.md),
[backtesting.md](backtesting.md).
