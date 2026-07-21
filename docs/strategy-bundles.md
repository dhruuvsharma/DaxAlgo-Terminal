# Signed strategy bundles (`.daxstrategy`)

> Windows strategy-distribution and isolated-backtest format. Immutable installation and exact worker
> activation are implemented; marketplace integration, automatic updates, and live-host loading remain
> later slices. Existing `.daxplugin` behavior stays a separate legacy path.

A `.daxstrategy` is one portable file, not one merged DLL. It is a passive ZIP whose contents can be
inspected and verified without executing them. Strategy math stays in one canonical WPF-free engine;
optional WPF presentation is a companion. Live and backtest consumers will use the same engine rather
than maintain separate strategy implementations, following
[ADR-0010](../.claude/context/adr/ADR-0010-isolated-backtest-worker.md).

Linux/Avalonia packaging and loading are outside v1 scope.

## Layout

```text
bundle.manifest.json
payload/
  engine/Acme.MeanReversion.Engine.dll
  windows/Acme.MeanReversion.Wpf.dll       # optional
  deps/Acme.Numerics.dll                   # optional private managed dependency
  resources/model.onnx                     # optional non-executable resource
  sbom/component.spdx.json                 # optional
  provenance/build.json                    # optional
signatures/
  publisher.dsse.json                      # optional; added by sign
```

The exact manifest, role roots, and publisher-signature path are format data. A renamed `.daxplugin`, a
legacy root `package.json`, an unlisted entry, a path/role mismatch, or an unsupported manifest version is
rejected.

Payload roles are:

- `engine`: exactly one managed, IL-only assembly containing the canonical strategy kernel;
- `windows-ui`: zero or one managed, IL-only Windows presentation companion;
- `managed-dependency`: an explicitly listed private managed dependency;
- `resource`, `sbom`, and `provenance`: non-executable supporting material.

Managed payloads cannot be native, mixed-mode, ReadyToRun, or AOT binaries. Host `TradingTerminal.*` and
`DaxAlgo.Sdk*` assemblies cannot be bundled as private copies. The engine and all private dependencies
must remain WPF/UI-free; duplicate assembly identities are rejected. The manifest records the exact
managed path/name/reference graph, recomputed from PE metadata during verification. These are
metadata-only checks: the verifier never loads a payload assembly. Although v1 serializes simple names in
that graph, verification also matches every private reference to the bundled definition's full version,
culture, public-key token, Windows Runtime content type, and retargetable flag.

The engine object names one exact assembly path and one public, parameterless type implementing
`DaxAlgo.Sdk.IStrategyEngineFactory`. Its closed v1 contract is
`daxalgo.strategy-engine-factory/1` with `public-parameterless-constructor` activation. This lets a future
live host or worker create the same kernel without scanning assemblies, guessing constructors, or asking
the strategy author for a second backtest implementation.

The factory exposes one declarative `StrategyParameterSchema`, one `StrategyDataRequirement`, and a
parameterized `Create(Contract, StrategyParameters)` method. Live replay, a single backtest, and optimizer
sweeps therefore select values through the same activation seam and execute the same kernel. A strategy
with no tunables uses `StrategyParameterSchema.Empty`.

## Canonical manifest and identity

`bundle.manifest.json` uses a closed canonical UTF-8 encoding. Property order is fixed, text is NFC,
capabilities and payload paths are sorted, SHA-256 values are lowercase, insignificant whitespace is
absent, and unknown or duplicate properties are invalid. The format owns its JSON escaping algorithm:
quotes, backslashes, and controls use fixed escapes while valid non-ASCII scalars are emitted directly as
UTF-8, independent of runtime JavaScript-encoder block lists.

The schema records:

- format and format version;
- stable strategy id, display name, strategy version, and publisher id;
- target SDK plus optional minimum/maximum host versions;
- the exact engine assembly, factory type, contract, and activation convention;
- every managed assembly's path, simple identity, and referenced assembly names;
- declared capability identifiers;
- every payload's relative path, role, byte length, and SHA-256.

An abbreviated example is:

```json
{"format":"daxstrategy","formatVersion":1,"identity":{"id":"acme.mean-reversion","name":"Acme Mean Reversion","publisherId":"acme-research","version":"1.2.0"},"compatibility":{"targetSdkVersion":"0.2.0-alpha","minimumHostVersion":"1.0.0","maximumHostVersion":null},"engine":{"assemblyPath":"payload/engine/Acme.MeanReversion.Engine.dll","typeName":"Acme.MeanReversion.Engine.StrategyFactory","contract":"daxalgo.strategy-engine-factory/1","activation":"public-parameterless-constructor"},"managedAssemblies":[{"path":"payload/engine/Acme.MeanReversion.Engine.dll","name":"Acme.MeanReversion.Engine","references":["DaxAlgo.Sdk","System.Runtime"]}],"capabilities":["market-data.bars"],"payloads":[{"path":"payload/engine/Acme.MeanReversion.Engine.dll","role":"engine","length":48128,"sha256":"0123456789abcdef..."}]}
```

The unsigned content identity is:

```text
sha256(exact canonical bytes of bundle.manifest.json)
```

Because the manifest hashes and sizes every payload, that digest commits to all content, roles, identity,
and compatibility metadata. It excludes local paths, wall-clock time, random ids, signatures, and future
attestations. The packer uses ordinal entry order, fixed timestamps, no ZIP comments, and uncompressed
entries, so identical inputs produce identical unsigned archive bytes as well as the same content root.

Adding the publisher signature changes the archive bytes but preserves the content root. A raw archive
hash can protect a download; it is not the strategy identity.

## Publisher evidence

`signatures/publisher.dsse.json` is a DSSE envelope whose payload is the exact canonical manifest. V1
signs standard DSSE pre-authentication bytes with ECDSA P-256/SHA-256 and IEEE-P1363 signature encoding.
The envelope uses the standard DSSE `keyid` field. That id is resolved to a public
SubjectPublicKeyInfo supplied through an independently trusted channel and explicitly bound to the
manifest's `publisherId`; trusting the same key id for a different publisher is insufficient. Using the
SPKI SHA-256 fingerprint as the key id is recommended.

A verified signature means only:

> The holder of this key endorsed the manifest and every payload byte it identifies.

That establishes publisher authenticity for the signed content, not code safety. The exact outer ZIP is
identified separately by its archive SHA-256 in store/client evidence; together, the verified publisher
signature and that archive hash preserve which endorsed content and archive were selected. Neither claim
establishes safety, profitability, marketplace review, or process isolation. A signature carrying a key
id that is not trusted for the manifest publisher is reported as unknown, not as a verified publisher;
without that trusted key, the verifier cannot claim the signature is valid.

A marketplace attestation will be a separate later claim over the same content root and named review
policy. Its envelope, trusted-key registry, freshness, rotation, and revocation model are deferred to the
marketplace integration slice. The current verifier rejects unexpected archive entries.

## CLI workflow

Build engine and optional UI with the normal .NET compiler, then use the portable tool:

```powershell
dotnet build -c Release

daxalgo-bundle pack `
  --id acme.mean-reversion `
  --name "Acme Mean Reversion" `
  --version 1.2.0 `
  --publisher acme-research `
  --sdk 0.2.0-alpha `
  --engine .\bin\Acme.MeanReversion.Engine.dll `
  --entry-type Acme.MeanReversion.Engine.StrategyFactory `
  --ui .\bin\Acme.MeanReversion.Wpf.dll `
  --dependency .\bin\Acme.Numerics.dll `
  --output .\Acme.MeanReversion.daxstrategy

daxalgo-bundle sign `
  --bundle .\Acme.MeanReversion.daxstrategy `
  --key .\publisher-private.pem `
  --key-id acme-2026

daxalgo-bundle verify `
  --bundle .\Acme.MeanReversion.daxstrategy `
  --public-key .\publisher-public.pem `
  --publisher acme-research `
  --key-id acme-2026

daxalgo-bundle inspect --bundle .\Acme.MeanReversion.daxstrategy
```

`pack` creates an unsigned deterministic bundle. `sign` adds publisher evidence and safely rewrites only
after a complete output has been flushed. `verify` requires the matching trusted public key. `inspect`
checks archive structure, canonical metadata, payload hashes, and signature presence without claiming an
unverified signature is authentic. None of these commands loads strategy code.

`pack` and `sign --output` refuse to replace any existing destination. Only `sign` without `--output`
may atomically replace the exact validated input bundle in place. The final move is non-overwriting in
every other case, so path aliases through junctions, symlinks, or hard links cannot clobber a payload or
private key.

Private key material is accepted from a PEM file or standard input, never inline as an option value and
never printed. Prefer a protected key file, hardware/OS key service, or CI secret provider. Publisher and
future marketplace keys represent different claims and must have separate custody. Plan rotation and
revocation before publishing.

## Fail-closed behavior

The verifier rejects:

- unsupported format versions, non-canonical or duplicate JSON properties, and unknown fields;
- compressed-size, entry-count, expanded-size, ratio, path-length, or nesting limit violations;
- traversal, absolute/ADS/reserved paths, directory/link/reparse/special-file entries, case or Unicode
  aliases, and file/descendant path conflicts;
- unlisted, missing, length-mismatched, or hash-mismatched payloads;
- zero/multiple engines, multiple UI companions, or a path inconsistent with its declared role;
- native, mixed-mode, ReadyToRun/AOT, bundled host assemblies, duplicate managed identities, executable
  resources, graph/metadata mismatches, missing/invalid engine factories, or engine/dependency WPF/UI
  references;
- malformed signature envelopes or signatures that fail for the supplied trusted key.

Successful offline inspection is not permission to run code. Installation and activation apply an
explicit current host/SDK and publisher policy as described below. Publisher registration and
revocation-freshness distribution still belong to marketplace integration. If a future live host loads
the engine in-process, it still has that process's authority; see
[ADR-0009](../.claude/context/adr/ADR-0009-out-of-process-strategy-host.md).

## Immutable install and isolated backtest activation

The strategy store separates content from evidence:

```text
strategy-store/
  objects/sha256/<content-root>/
    bundle.manifest.json
    payload/...
  evidence/sha256/<archive-sha256>/
    bundle.daxstrategy
    install.receipt.json
  activations/<strategy-id>.json
  .staging/...
```

The content object contains the canonical manifest and every declared payload. The evidence object holds
one exact outer archive and a canonical structured receipt. Because signing changes the archive but not
the content root, differently signed copies reuse the immutable content object while keeping independent
evidence. Install collisions are verified and reused; existing bytes are never overwritten.

Two explicit trust lanes exist. `LocalDevelopment` accepts an unsigned local bundle. A present signature
must still verify, so an unknown or invalid signature cannot be relabeled as local.
`RequireVerifiedPublisher` requires a key explicitly bound to the manifest publisher. Install,
activation, and resolve also enforce exact target SDK and inclusive host-version bounds and re-check the
archive, receipt, manifest, every payload, and exact file tree. Activation publishes only a small atomic
pointer and re-evaluates current policy; historical receipt text is not a trust anchor. Verified evidence
retains both the envelope key id and the SHA-256 fingerprint of the exact trusted SPKI.

Backtest protocol v2 represents this as an `installed_bundle` strategy source with exact content and
archive hashes, an exact strategy-assembly hash, typed integer, number, boolean, choice, and text
parameters, and closed trust evidence. That evidence distinguishes unsigned local development from a
verified publisher and, for the latter, carries the key id, trusted-SPKI fingerprint, and signature
algorithm. Separately, the request pins the exact deployed host `TradingTerminal.Backtest.Engine`
assembly hash. The host client resolves the immutable install under current policy, then copies only
`bundle.manifest.json` and the graph-reachable engine/private-dependency closure into the worker job.
Optional Windows UI, unrelated dependencies, resources, receipts, and store paths are not staged.

Before any strategy constructor runs, the worker revalidates the canonical manifest, identity, SDK,
factory contract, file set, lengths, hashes, managed metadata, and dependency closure. A collectible load
context resolves private dependencies only from those owned byte arrays. It accepts an external reference
only when the exact assembly is actually present in the worker runtime's trusted-platform assembly set or
is an explicitly shared SDK/Core contract; desktop assemblies available elsewhere in the terminal are not
implicitly allowed. It rejects unmanaged loading and directory probing and activates the exact manifest
type. Runtime values must exactly match the factory's declarative schema before the same
`IBacktestStrategy` implementation is adapted into the canonical engine. The strategy and load context
are disposed after the one-shot run.

The result's `StrategyAssemblyClosure` records that verified, staged engine closure. It is evidence of the
files made available to the strategy load context, not a claim that every listed assembly was actually
loaded. The result separately reports the actual host-engine and strategy-assembly hashes and echoes the
request's trust evidence; the client compares all of them before accepting success. `StrategyArchiveSha256`
is store/client selection evidence for the exact installed archive; the worker does not receive or inspect
that archive.

The collectible load context and one-shot process tree under a Windows Job Object provide lifecycle and
fault isolation, not an OS security sandbox. Bundle code still has the same user token and can use that
account's filesystem, network, and framework permissions. Marketplace execution must therefore remain
trusted-publisher-only until an approved restricted-token/AppContainer or VM boundary exists, or until
strategy code runs in a separately constrained child that exposes only signal IPC. Signature
verification is not a substitute for any of those execution controls.

## Migration from `.daxplugin`

Do not rename or blindly re-sign a legacy package. Rebuild the canonical WPF-free engine, split optional
WPF presentation, pack a new manifest identity, and sign that exact content root. Repacking cannot create
publisher ownership or marketplace provenance.

Current Plugin Manager, feed, updater, uninstaller, and live strategy host continue to use the legacy
format. The new immutable store and backtest-worker route are opt-in APIs until installed-strategy
discovery, trust-registry, and parameter-schema UX are connected in the next UI/marketplace slice.

See also: [ADR-0011](../.claude/context/adr/ADR-0011-signed-strategy-bundles.md) ·
[ADR-0012](../.claude/context/adr/ADR-0012-immutable-strategy-install-and-worker-activation.md) ·
[plugin security](plugin-security.md) · [plugin authoring](plugin-authoring.md) ·
[marketplace hosting](marketplace-hosting.md).
