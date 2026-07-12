# Plugin marketplace policy

> Last updated: 2026-07-12 · Applies to plugins submitted to the DaxAlgo plugin feed.

This is the rulebook for publishing a strategy plugin through the curated DaxAlgo feed. It exists so that
what a user installs from the catalog is source-reviewed, signed, and can be revoked if it turns out to
misbehave. Distributing a plugin *outside* the feed (a raw `.daxplugin` you hand someone) is not covered
by this policy — but it also won't be signed, so it loads only under Permissive/consent (see
[plugin-security.md](plugin-security.md)).

## What listing requires

1. **Source, submitted.** Plugins are built **from source** by the submission CI (see below), not from a
   binary you upload. The published package's hash is reproduced from your source, so the bytes users get
   are the bytes that were reviewed.
2. **A manifest with declared, justified permissions.** `plugin.json` must declare every Warn-level
   capability the plugin uses (`fileIo`, `network`, `environment`) and the PR must justify each. An
   undeclared capability the scanner finds is a rejection.
3. **Data / signals only.** Like the terminal itself, a plugin computes signals and reads market data. It
   must not place live orders or attempt to.
4. **Passing the gates.** The submission must build, its tests must pass, and the **IL policy scan** must
   report no Block-level capability (see forbidden behaviours).

## Forbidden behaviours (aligned 1:1 with the scanner's Block list)

A plugin may **not**, anywhere in its assemblies:

- P/Invoke or call unmanaged code;
- start or inspect OS processes (`System.Diagnostics.Process`);
- read or write the Windows registry;
- generate code at runtime (`System.Reflection.Emit`) or load further assemblies (`Assembly.Load*`,
  `AssemblyLoadContext`);
- read or write files **outside the plugin's own folder**, or open network connections, **unless** that
  capability is declared in `plugin.json`, justified in the PR, and approved by a reviewer.

These map exactly to the host's static scan, so a plugin that violates them fails CI before review.

## Versioning & update duties

- Pin `DaxAlgo.Sdk`(`.Wpf`) to the SDK you build against; set `plugin.json`'s `targetSdkVersion` to the
  same value. Pre-1.0, the host requires an **exact major.minor** match — an SDK minor bump orphans your
  plugin until you rebuild and re-submit.
- Bump your plugin's `version` on every functional change; the feed keeps history and users see an update
  badge on a higher version.
- Security-relevant fixes should be submitted promptly; the feed can revoke an old build (below).

## Takedown & revocation

A build found to be malicious, broken, or non-compliant is added to the feed's `revoked` list (by package
sha256, or by plugin id for all builds). On the next feed refresh the app quarantines installed copies and
shows a notice. Revocation is a safety mechanism, not a punishment — an author can fix and re-submit.

## Naming & impersonation

- Don't publish under a name or publisher that impersonates another author, DaxAlgo, or a third party.
- Don't claim performance you can't substantiate in the description; a `paperUrl` should point at the
  actual paper.
- Ids are first-come; a squatted or misleading id can be reassigned by the maintainer.

## Licensing

Your plugin links the `DaxAlgo.Sdk` (MIT), which in turn links `TradingTerminal.Core` (AGPL-3.0). A
closed-source plugin is only permissible under the **plugin linking exception** in
[LICENSE-EXCEPTIONS.md](../LICENSE-EXCEPTIONS.md). If that exception is not yet in force, plugins must be
open-source-compatible. Read it before submitting a proprietary plugin.

## What isn't covered here

Payments, entitlement, and licensing *enforcement* are out of scope for the feed — it's a signed static
index of installable packages, nothing more. Any such layer is a separate decision.

See also: [plugins.md](plugins.md) · [plugin-authoring.md](plugin-authoring.md) ·
[plugin-security.md](plugin-security.md).
