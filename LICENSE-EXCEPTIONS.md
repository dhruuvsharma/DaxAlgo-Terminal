# DaxAlgo Terminal — additional licensing permissions

> **STATUS: DRAFT — not yet in force.** The plugin linking exception below takes effect only when the sole
> copyright holder (Dhruv Sharma) has reviewed and signed off on this text. Until then, do **not** rely on
> it for a closed-source plugin. This file is versioned so the exact terms in force at any commit are
> auditable.

## Why this exists

`TradingTerminal.Core` and the other contract assemblies bundled by the plugin SDK are licensed
**AGPL-3.0-only**. The plugin SDK (`DaxAlgo.Sdk`, `DaxAlgo.Sdk.Wpf`) is MIT, but it links those AGPL
assemblies, and the host loads a plugin into the same process. Absent an explicit exception, a plugin that
consumes the SDK is arguably a derivative work of the AGPL core, which would force **every** plugin —
including proprietary, closed-source ones — to be AGPL. That would make a commercial third-party plugin
ecosystem impossible.

Because Dhruv Sharma is the **sole copyright holder** of the AGPL code, he can grant an *additional
permission* under AGPL-3.0 §7 (a "linking exception", in the spirit of the GNU Classpath exception) that
lets plugins link the SDK without themselves becoming AGPL.

## Plugin Linking Exception (draft)

> As an additional permission under section 7 of the GNU Affero General Public License version 3, the
> copyright holder of DaxAlgo Terminal grants you permission to link or combine a **DaxAlgo Terminal
> strategy plugin** with the covered work through the published plugin SDK surface (`DaxAlgo.Sdk`,
> `DaxAlgo.Sdk.Wpf`, and the contract types they re-export), and to convey the resulting plugin under
> terms of your choice, **provided that**:
>
> 1. the plugin interacts with the covered work **only** through that published SDK surface (the
>    `IStrategyPlugin` / `IBacktestStrategy` / `ITradingStrategy` / parameter / market-data contracts and
>    the WPF strategy-window base), and does not otherwise copy, modify, or statically incorporate the
>    covered work's source;
> 2. you do not remove or alter any license or copyright notices in the covered work or the SDK; and
> 3. this permission does **not** extend to modifications of the covered work itself (the terminal, the
>    Core/UI/Infrastructure assemblies) — those remain governed by AGPL-3.0-only, and a modified terminal
>    conveyed to users must still offer its Corresponding Source.
>
> This additional permission applies only to plugins as described above. It does not grant any rights to
> the covered work beyond linking through the SDK, and it may be extended (never retroactively narrowed
> for already-conveyed plugins) by the copyright holder in a future version of this file.

## Scope notes (informational, not part of the grant)

- The exception is about **linking**, not about the terminal's own AGPL obligations: if you modify the
  *terminal* and distribute or run it as a network service, the AGPL source-offer requirement is unchanged.
- A plugin distributed **outside** the curated feed is still bound by this exception's conditions if it
  links the SDK; the feed's [marketplace policy](docs/marketplace-policy.md) adds review/signing on top.
- MIT applies to the SDK packages themselves; this file only concerns the AGPL *linked* code.

---

*Sign-off:* pending. When approved, replace this line with the approving commit and remove the DRAFT
banner. The SDK package readme and `docs/marketplace-policy.md` reference this file.
