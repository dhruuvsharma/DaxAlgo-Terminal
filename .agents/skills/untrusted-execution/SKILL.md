---
name: untrusted-execution
description: Security playbook for running third-party or user-supplied code inside DaxAlgo Terminal. Use when editing src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/, the tree-equivalent Linux sandbox, ISandboxRunner, SandboxPolicy/SandboxQuota, paper reproduction, or any feature that executes untrusted code. Enforces deny-by-default isolation, egress controls, quotas, safe process-tree termination, and no access to host stores or credentials.
---

# Untrusted Execution (the sandbox)

Running a research paper's real code means running **arbitrary, untrusted code from arbitrary repos**.
That is a remote-code-execution surface. The rule is simple: **untrusted code runs only inside a
disposable container/VM, deny-by-default, and the C# side only orchestrates the sandbox over a
process boundary — it never `exec`s paper code in-process.**

## Threat model

The reproduced code is hostile until proven otherwise. It may try to: exfiltrate the canonical store
or broker credentials, beacon out over the network, fork-bomb / exhaust RAM/disk, persist on the
host, or pivot to the broker sessions. Every mitigation below maps to one of these.

## Non-negotiable controls (deny-by-default)

`ISandboxRunner.RunAsync(ReproSpec, SandboxQuota, SandboxPolicy, IProgress<string>, ct)` must enforce:

- **Isolation**: a container (Docker first) or VM/WSL2 — never the host process, never `Process.Start`
  of the paper's entrypoint directly. The runner spawns the *container CLI*, not the paper code.
- **No host mounts** of anything sensitive: NO bind-mount of the market-data store dir, the credential
  store, the user profile, or the repo tree's parent. Only a **scratch tmpfs/volume** is writable.
- **Read-only rootfs** + tmpfs scratch; drop Linux capabilities (`--cap-drop ALL`); `--pids-limit`;
  non-privileged user; no `--privileged`, no docker socket mount.
- **Network deny-by-default**: `--network none`. The `SandboxPolicy` egress allowlist is opt-in and
  scoped to the paper's *declared* data deps only — never "allow all".
- **Quotas** from `SandboxQuota`: `--memory`, `--cpus`, disk cap, and a wall-clock timeout. On
  timeout/cancel, **kill the entire process tree** (clone `LeanProcessRunner`'s kill-tree).
- **Concurrency bound**: a `SemaphoreSlim` from `SandboxOptions.MaxConcurrent` — one runaway job must
  not starve the box.
- **One-way data flow out**: results leave only as a declared artifact file in the scratch volume,
  read back and validated by `IReproSignalBridge`. The sandbox cannot write anywhere else.

## Layer + build placement

- `SandboxPolicy` / `SandboxQuota` / `SandboxKind` are `Core/Research/` records/enums (inputs to the
  runner, deny-by-default constructors).
- `DockerSandboxRunner` / `Wsl2SandboxRunner` / `SandboxProcess` / `RepoFetcher` live in
  `Infrastructure/Research/Sandbox/`. Selected by `SandboxOptions.Kind` in `AddPaperResearch`.
- **No Python/native deps in the C# build** — env-resolution + minimal-repro LLM logic live in the
  Python sidecar (`tools/python-ml/`); the sidecar itself binds `127.0.0.1` only and is also outside
  the container's egress allowlist.

## Cost + reliability

- The minimal repro runs first; `ReplicationCostEstimator` extrapolates its wall-clock/RAM to the
  full config (autoarxiv's "replication cost"). The full run is gated behind a budget the user
  approves — never auto-escalate.
- Sandbox failures fold into a `Failed` `ReproResult` (never throw across the seam), same never-throw
  contract as `IAiAnalystClient`.

## Verifier checklist (the deterministic invariants)

- [ ] No in-process exec of untrusted code — only via `ISandboxRunner` over a container/VM boundary.
- [ ] No host mount of the canonical store, credential dir, or user profile; scratch volume only.
- [ ] `--network none` unless the policy allowlist explicitly scopes egress; never allow-all.
- [ ] CPU/RAM/pids/disk/wall-clock quotas set; kill-process-tree on timeout/cancel.
- [ ] Concurrency bounded; runner picked by `SandboxOptions.Kind`.
- [ ] Sidecar + any helper services bound to `127.0.0.1`.

## What NOT to do

- Don't bind-mount a host path "just to make it work" — that's the exfiltration hole.
- Don't default the egress allowlist to open, or skip it "for now".
- Don't run the repo on the host even once "to test" — there is no trusted first run.
- Don't let the sandbox write outside the scratch volume; don't read its stdout as the result —
  read the declared, validated artifact.
- Don't throw across `ISandboxRunner` — fold failures into `ReproResult.Failed`.
