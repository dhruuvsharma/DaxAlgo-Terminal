# Plugin security — what protects you, and what doesn't

> Last updated: 2026-07-12

This page is the honest threat model for strategy plugins. If you just want to install a plugin, start
with [plugins.md](plugins.md); come back here to understand what the app's checks do and do not
protect you from.

## The uncomfortable truth

A strategy plugin is a .NET assembly that runs **inside the terminal's process**. It needs low-latency
access to live market data, so it is not — and on modern .NET *cannot* be — sandboxed. While it runs it
has the same power the terminal itself has:

- it can read your **broker session** and anything the terminal can request from your broker;
- it can read your **saved credentials** (the credential store is DPAPI-protected at rest, but any code
  in the process can ask the terminal for what it has already unlocked);
- it can read and write your **market-data stores** and any file your user account can;
- it can start processes, call native code, and open network connections.

There is no in-process setting that takes this away. So the app's real defense is **not running
untrusted code in the first place** — curation and code signing — backed by layers that make careless
or casual abuse fail loudly. Everything below is either *that curation* or a *tripwire*, and we say so
plainly rather than implying a sandbox that doesn't exist.

> The one hard limit that is structural: this build has **no live order-execution path** (ADR-0007). A
> plugin can compute signals and read data; it cannot place a trade through the terminal.

## What actually protects you

| Layer | What it does | What it is |
|---|---|---|
| **Curation + signing** | Only plugins signed by a publisher the host pins load in Curated mode. | **The control.** Everything else is secondary. |
| **Hash-pinned first-party trust** | The build records the sha256 of every strategy it ships; those load without a certificate, and any change to them is caught. | Trust anchor + integrity. |
| **Integrity check** | Every load re-hashes each assembly; a shipped or installed plugin that was modified, swapped, added to, or deleted is quarantined as *tampered*. Runs in every mode. | Detection. |
| **Revocation** | A local kill-list (`revoked.json`, synced from the marketplace) refuses a build found to be malicious after you installed it. | Detection. |
| **Add-only registrar guard** | A plugin may add its own strategy, but may **not** replace a host service like the credential store or broker selector. Attempting it quarantines the plugin with nothing registered. | Deterrence + attribution. |
| **Static IL policy scan** | Before any plugin code runs, its bytecode is read as data. P/Invoke, starting processes, the registry, `Reflection.Emit`, and loading assemblies are **blocked**; file and network I/O are shown to you. | Tripwire. |
| **Unsigned-plugin consent** | A plugin that is neither ours nor signed by a pinned publisher does not load until you say so — after being shown what it is and what its code reaches for. Your answer is remembered per exact build. | Informed choice. |

Trust modes: **Curated** is the shipped default; **Permissive** (used only by the developer launch
profiles) loads unsigned local plugins without inspecting signatures — the integrity check and the IL
scan still run.

## What these layers do NOT protect you from

Be clear-eyed about the limits:

- **A determined attacker with a signing certificate.** Signing proves *who* wrote a plugin, not that
  the plugin is safe. Curation is only as good as who you trust to sign.
- **A payload hidden behind reflection.** The IL scan reads static references. Code that builds a
  forbidden call out of strings at runtime, or hides it inside a bundled dependency the scan only
  *warns* about, can slip past it. The scan is a tripwire against lazy or accidental abuse, **not** a
  sandbox.
- **Anything an unsigned plugin does once you consent to it.** Consent means *you* decided to run
  untrusted code. From that point it has the process's full power. The **DEV (unsigned)** badge on its
  catalog card is a permanent reminder, not a leash.
- **The runtime authoring pane.** Strategies you (or an AI) write in the app compile straight into the
  process. They pass the same IL scan — a P/Invoking authored strategy fails to compile — but they are
  unsigned by definition and run on the same footing as a consented plugin. They are marked *DEV
  (unsigned)*.

## The DEV (unsigned) badge

Any strategy that is running because you consented to it — a dropped-in unsigned plugin, or one written
in the authoring pane — wears a **DEV (unsigned)** badge on its catalog card and in the Plugin Manager.
It means: *this code was not verified by a pinned publisher; it runs because you allowed it.* If you see
that badge on a strategy you don't recognize, disable it in the Plugin Manager.

## What we're honest about not having yet

True enforcement — running untrusted strategy code in a **separate process** with a restricted token,
so a hostile plugin physically cannot reach your credentials or data — is specified in **ADR-0009** but
not built. Until it is, the honest posture is the one above: curation is the control, the rest are
tripwires, and consent means you accepted the risk.

See also: [plugins.md](plugins.md) (installing and building plugins) ·
[architecture.md](architecture.md) · ADR-0007 (no live order execution) · ADR-0008 (plugin model) ·
ADR-0009 (out-of-process host).
