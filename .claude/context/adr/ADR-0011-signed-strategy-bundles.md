# ADR-0011 — Signed strategy bundles

**Date** 2026-07-21 · **Status** accepted

**Context.** The legacy Windows `.daxplugin` format has separate package, installed-assembly,
publisher-signature, and marketplace identities. It also predates ADR-0010's decision that one WPF-free
engine is the behavioral oracle and WPF presentation is a separate companion. Renaming that format or
merging engine and UI into one executable DLL would preserve those problems.

**Decision.** The shared Windows strategy distribution format is `.daxstrategy` v1: one passive ZIP
container, never a merged assembly, executable installer, or self-extracting program. It contains exactly
one canonical WPF-free engine assembly, zero or one WPF companion, and explicitly declared supporting
payloads. The UI may depend on the engine; the engine must not depend on WPF or UI assemblies.

V1 has a new, fail-closed identity:

- the required root manifest is exactly `bundle.manifest.json`;
- payloads use the role-bound roots `payload/engine/`, `payload/windows/`, `payload/deps/`,
  `payload/resources/`, `payload/sbom/`, and `payload/provenance/`;
- the optional publisher envelope is exactly `signatures/publisher.dsse.json`;
- a root `package.json`, legacy layout, unlisted entry, or unsupported manifest format/version is rejected
  rather than guessed or upgraded.

The manifest uses a closed, format-defined canonical UTF-8 JSON encoding: fixed property order, NFC text,
sorted capabilities and payload paths, lowercase digests, no insignificant whitespace, and no unknown or
duplicate properties. Its fixed string encoder writes valid non-ASCII scalars directly as UTF-8 and does
not depend on runtime JavaScript-encoder block lists. It identifies the strategy and publisher, declares
SDK/host compatibility and capabilities, names one exact public `DaxAlgo.Sdk.IStrategyEngineFactory`,
records the managed assembly path/name/reference graph, and lists every payload path, role, byte length,
and SHA-256 digest. The closed v1 factory contract is `daxalgo.strategy-engine-factory/1` with public
parameterless activation. Its instance members expose the parameter schema and data requirements and
create the canonical kernel from a `Contract` plus `StrategyParameters`, so live replay, single runs,
and optimizer sweeps use one implementation. The deterministic unsigned content identity is
`sha256(bundle.manifest.json canonical bytes)`.

The manifest excludes wall-clock timestamps, random values, machine paths, signatures, and attestations.
The writer uses ordinal entry order, fixed timestamps, no comments or extra fields, and uncompressed
entries. Therefore identical metadata and payload bytes produce identical unsigned archive bytes and the
same content root. A raw archive SHA-256 can still protect transport, but it is not strategy identity.

Publisher approval is a separate `signatures/publisher.dsse.json` envelope. Its payload is the exact
canonical manifest, its payload type is `application/vnd.daxalgo.strategy-manifest.v1+json`, and its
signature covers standard DSSE pre-authentication encoding using ECDSA P-256/SHA-256 with IEEE-P1363
signature bytes. The envelope carries the standard DSSE `keyid`, resolved to an independently trusted
public SubjectPublicKeyInfo and explicitly bound to the manifest publisher id; using the SPKI SHA-256
fingerprint as that id is recommended.

A publisher signature proves only that the holder of that key endorsed the exact content root. It does
not prove safety, review, profitability, isolation, or marketplace approval. A later marketplace review
will produce a distinct attestation over the same content root and policy/build evidence. Its envelope
path, freshness, key registry, rotation, and revocation metadata remain deferred to marketplace
integration; the current verifier rejects unexpected entries. Compile, pack, publisher-sign, and
marketplace-attest remain separate operations and claims.

Verification is passive. It parses ZIP, canonical JSON, and managed PE metadata, hashes bytes, and checks
publisher evidence without loading an assembly or executing bundle code. Archive/resource limits, unsafe
or aliased paths, payload mismatches, native/mixed-mode/ReadyToRun executable payloads, bundled host SDK
assemblies, duplicate identities, managed-graph mismatches, invalid factory metadata, and WPF/UI
references anywhere in the engine dependency closure fail closed. Host compatibility and policy-required
evidence are enforced later by installer/runtime integration. Inspection can report a structurally valid
unsigned bundle, but inspection is not approval to install or run it.

This decision makes no sandbox claim. A valid signature establishes origin and integrity only. If a
future runtime loads a strategy in-process, ADR-0009 still applies: it runs with the host process's
authority. Process isolation is a separate execution decision.

**Consequences.** One shareable file can carry the canonical engine and optional Windows presentation
without duplicating strategy logic. One manifest digest aligns packaging, signing, future attestation,
installation, and revocation. V1 requires pack/sign/verify/inspect tooling, managed-only validation,
tests, and an explicit migration path.

Runtime loading, installation, update/uninstall, feed integration, and marketplace UI are deferred.
Legacy `.daxplugin` remains distinct and is never accepted by changing its extension. Linux/Avalonia is
out of scope; a WPF-free engine does not itself create a Linux compatibility promise.
