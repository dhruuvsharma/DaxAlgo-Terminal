using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research.Sandbox;

/// <summary>
/// Runs untrusted paper code inside a disposable Docker container with deny-by-default isolation. This
/// is a security boundary, not a feature — see the <c>untrusted-execution</c> skill. The runner spawns
/// the <em>docker CLI</em> (never the paper entrypoint in-process) and enforces every non-negotiable
/// control:
///
/// <list type="bullet">
/// <item><c>--network none</c> unless the policy allowlist explicitly scopes egress (never allow-all).</item>
/// <item><c>--read-only</c> rootfs; the ONLY writable surface is a size-bounded <c>tmpfs</c> at
/// <c>/scratch</c> (and a <c>noexec</c> tmpfs <c>/tmp</c>). There is NO host bind-mount at all — not the
/// store, not credentials, not the user profile, not even a scratch dir. The repo payload is copied IN
/// with <c>docker cp</c> and the result artifact copied OUT with <c>docker cp</c>, so the container can
/// never reach the host filesystem.</item>
/// <item><c>--cap-drop ALL</c>, <c>--security-opt no-new-privileges</c>, non-root <c>--user</c>.</item>
/// <item><c>--memory</c> / <c>--cpus</c> / <c>--pids-limit</c> set from the quota; the scratch
/// <c>tmpfs</c> is capped at <c>SandboxQuota.DiskMb</c> so untrusted code cannot fill the host temp
/// partition; a wall-clock timeout kills the entire process tree and force-removes the container.</item>
/// </list>
///
/// <para><b>Disk-quota approach.</b> Windows Docker Desktop does not reliably honour
/// <c>--storage-opt size=</c> (it requires the <c>devicemapper</c>/btrfs storage driver, absent on the
/// default overlay2/WSL2 backend). A size-bounded <c>tmpfs</c> for the only writable mount caps disk
/// deterministically across drivers, so that is what we use — at the cost of giving up the host
/// bind-mount, which is why the repo/artifact move via <c>docker cp</c>.</para>
///
/// <para>Results leave only as the declared artifact file <c>/scratch/result.json</c>, copied back and
/// sha256-validated. Every failure folds into <see cref="ReproResult.Failed"/> — never throws.</para>
///
/// <para>All process spawns use <see cref="SandboxProcess"/>'s token-list argument API — no manually
/// quoted single argument string — so Windows paths and arguments can't be mangled or injected.</para>
/// </summary>
internal sealed class DockerSandboxRunner : ISandboxRunner
{
    /// <summary>The declared artifact the container must write; the only data-flow path out.</summary>
    private const string ArtifactFileName = "result.json";

    /// <summary>Mount point for the writable tmpfs scratch inside the container.</summary>
    private const string ContainerScratch = "/scratch";

    private readonly IOptionsMonitor<SandboxOptions> _options;
    private readonly ILogger<DockerSandboxRunner> _logger;
    private readonly Lazy<bool> _dockerAvailable;

    public DockerSandboxRunner(IOptionsMonitor<SandboxOptions> options, ILogger<DockerSandboxRunner> logger)
    {
        _options = options;
        _logger = logger;
        _dockerAvailable = new Lazy<bool>(ProbeDocker);
    }

    public SandboxKind Kind => SandboxKind.Docker;

    public bool IsAvailable => _dockerAvailable.Value;

    public async Task<ReproResult> RunAsync(
        ReproSpec spec,
        SandboxQuota quota,
        SandboxPolicy policy,
        IProgress<string> log,
        EnvResolutionPlan? plan = null,
        CancellationToken ct = default)
    {
        var arxiv = spec.Paper.ArxivId;
        var commit = spec.Repo.Commit;

        if (!IsAvailable)
            return ReproResult.Failed("Docker CLI is not available on this machine.", arxiv, commit);

        // Private host-side staging root. The repo is cloned here, then copied INTO the container with
        // `docker cp`. It is NEVER bind-mounted — the container cannot see it.
        var hostStaging = Path.Combine(Path.GetTempPath(), "daxalgo-repro", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(hostStaging);
        }
        catch (Exception ex)
        {
            return ReproResult.Failed($"Could not create staging dir: {ex.Message}", arxiv, commit);
        }

        // A unique container name so we can `cp`/`start`/`rm` it; force-removed in finally (kill-tree).
        var containerName = "daxalgo-repro-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            void Emit(string line) => log.Report(line);

            // 1) Clone the repo at the pinned commit into the host staging dir (trusted git CLI, kill-tree).
            var fetch = await RepoFetcher
                .FetchAsync(spec.Repo, hostStaging, Emit, quota.WallClock, ct)
                .ConfigureAwait(false);
            if (!fetch.Success || fetch.RepoDir is null)
                return ReproResult.Failed(fetch.Error ?? "Repo fetch failed.", arxiv, commit);

            // The plan (when resolved by the sidecar) selects the image + entrypoint; otherwise fall back
            // to the configured default image and a placeholder entrypoint. The plan changes only WHAT
            // runs — never the isolation flags below.
            var image = !string.IsNullOrWhiteSpace(plan?.Image) ? plan!.Image : _options.CurrentValue.BaseImage;
            Emit($"[docker create] image={image} network={(policy.IsNetworkDenied ? "none" : "allowlisted")} disk={quota.DiskMb}m");

            // 2) Create (don't run) the container with deny-by-default isolation so we can copy the repo
            //    in before it starts.
            var createArgs = BuildCreateArgs(containerName, image, quota, policy, plan);
            var create = await SandboxProcess
                .RunAsync("docker", createArgs, hostStaging, Emit, quota.WallClock, ct)
                .ConfigureAwait(false);
            if (create.NotFound)
                return ReproResult.Failed("docker CLI not found on PATH.", arxiv, commit);
            if (!create.Success)
                return ReproResult.Failed($"docker create failed: {Tail(create.Output)}", arxiv, commit);
            created = true;

            // 3) Copy the cloned repo INTO the container's tmpfs scratch (one-way, host → container).
            var cpIn = await SandboxProcess
                .RunAsync("docker",
                    new[] { "cp", fetch.RepoDir, $"{containerName}:{ContainerScratch}/repo" },
                    hostStaging, Emit, quota.WallClock, ct)
                .ConfigureAwait(false);
            if (!cpIn.Success)
                return ReproResult.Failed($"docker cp (repo in) failed: {Tail(cpIn.Output)}", arxiv, commit);

            // 4) Start the container attached, so logs stream and the exit code is observable; the
            //    wall-clock timeout kills the whole tree and the finally block force-removes the container.
            var run = await SandboxProcess
                .RunAsync("docker", new[] { "start", "--attach", containerName },
                    hostStaging, Emit, quota.WallClock, ct)
                .ConfigureAwait(false);
            if (!run.Success)
                return ReproResult.Failed(
                    $"Sandbox run exited non-zero ({run.ExitCode?.ToString() ?? "killed"}).", arxiv, commit);

            // 5) Copy the declared artifact OUT (container → host staging). Never trust stdout as the result.
            var hostArtifact = Path.Combine(hostStaging, ArtifactFileName);
            var cpOut = await SandboxProcess
                .RunAsync("docker",
                    new[] { "cp", $"{containerName}:{ContainerScratch}/{ArtifactFileName}", hostArtifact },
                    hostStaging, Emit, quota.WallClock, ct)
                .ConfigureAwait(false);
            if (!cpOut.Success || !File.Exists(hostArtifact))
                return ReproResult.Failed("Sandbox produced no declared artifact (result.json).", arxiv, commit);

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(hostArtifact, ct).ConfigureAwait(false); }
            catch (Exception ex) { return ReproResult.Failed($"Could not read artifact: {ex.Message}", arxiv, commit); }

            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // Persist the validated artifact bytes to a durable host location (the staging dir is
            // deleted in `finally`) so the IReproSignalBridge can read them back. Keyed by the
            // deterministic cache key so a re-run / cache hit resolves to the same path. This is host
            // data the container never sees — it is copied here AFTER the container is done.
            string? durablePath = TryPersistArtifact(spec.CacheKey, bytes);

            var artifact = new ReproArtifact(ArtifactFileName, sha, bytes.LongLength, durablePath);

            // Carry the plan's deterministic env hash (from the resolved lockfiles) into provenance. Fall
            // back to an artifact-derived hash only when no plan was supplied (no sidecar resolution).
            var envHash = plan is { EnvHash.IsNone: false } ? plan.EnvHash : new EnvHash(sha[..16]);

            return new ReproResult(
                Success: true,
                PaperArxivId: arxiv,
                RepoCommit: commit,
                EnvHash: envHash,
                Artifacts: new[] { artifact },
                CostEstimate: null,
                Error: null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker sandbox run failed");
            return ReproResult.Failed($"Sandbox run failed: {ex.Message}", arxiv, commit);
        }
        finally
        {
            if (created) await TryRemoveContainerAsync(containerName).ConfigureAwait(false);
            TryDeleteScratch(hostStaging);
        }
    }

    /// <summary>
    /// Build the <c>docker create</c> argument tokens. Every flag here is a security control — do not
    /// relax one without reading the <c>untrusted-execution</c> skill. There is no host bind-mount; the
    /// only writable surface is a size-bounded tmpfs, and the repo is copied in via <c>docker cp</c>.
    /// One token per list entry — no manual quoting (the Win32 arg parser mangles quoted Windows paths).
    /// </summary>
    private static IReadOnlyList<string> BuildCreateArgs(
        string containerName, string image, SandboxQuota quota, SandboxPolicy policy, EnvResolutionPlan? plan)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "create",
            "--name", containerName,
            // Egress is opt-in and scoped; an empty allowlist → --network none. Otherwise drop to a
            // user-defined bridge (per-host firewalling of the allowlist set up elsewhere), never host
            // networking. The bridge name is a constant, never derived from untrusted input.
            "--network", policy.IsNetworkDenied ? "none" : "daxalgo-repro-egress",
            "--read-only",                                   // immutable rootfs
            "--cap-drop", "ALL",                             // no Linux capabilities
            "--security-opt", "no-new-privileges",           // block setuid escalation
            "--user", "65534:65534",                         // nobody:nogroup — non-root
            $"--memory={quota.MemoryMb}m",
            $"--memory-swap={quota.MemoryMb}m",              // disable swap (== memory) so RAM cap is hard
            $"--cpus={quota.Cpus.ToString(inv)}",
            $"--pids-limit={quota.PidsLimit}",
            // The ONLY writable mounts are tmpfs. /scratch is the disk quota: a size-bounded tmpfs caps
            // disk deterministically on Windows Docker Desktop (--storage-opt is driver-dependent). It is
            // nosuid; it CANNOT be noexec because the copied-in repo runs from here. /tmp stays noexec.
            "--tmpfs", $"{ContainerScratch}:rw,nosuid,size={quota.DiskMb}m",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
            "-w", ContainerScratch,
            image,
            // Declared entrypoint: the sidecar-resolved setup commands + entrypoint, run from the
            // copied-in repo, emitting the declared artifact. Environment resolution is the sidecar's
            // job (static analysis only); we only RUN what it resolved. With no plan, fall back to the
            // legacy placeholder so the legacy path still behaves identically.
            "sh", "-c",
            BuildEntrypointScript(plan),
        };
        return args;
    }

    /// <summary>
    /// Assemble the in-container shell script from the resolved plan: <c>cd repo</c>, run the plan's
    /// setup commands (best-effort dependency install), then the plan's entrypoint. The
    /// <c>RESULT_JSON</c> env var is exported so a plan's entrypoint can address the declared artifact
    /// without the runner string-substituting into untrusted command text. With no plan, fall back to
    /// the legacy placeholder. The script is built from plan strings only — no untrusted repo content is
    /// interpolated here (the plan is the sidecar's static output, not repo code).
    /// </summary>
    private static string BuildEntrypointScript(EnvResolutionPlan? plan)
    {
        var artifactPath = $"{ContainerScratch}/{ArtifactFileName}";
        if (plan is null || plan.IsEmpty)
            return $"python repo/repro.py --out {artifactPath}";

        var lines = new List<string>
        {
            "set -e",
            $"export RESULT_JSON={artifactPath}",
            "cd repo",
        };
        lines.AddRange(plan.SetupCommands.Where(c => !string.IsNullOrWhiteSpace(c)));
        lines.Add(plan.Entrypoint);
        return string.Join("\n", lines);
    }

    private static bool ProbeDocker()
    {
        try
        {
            var outcome = SandboxProcess
                .RunAsync("docker", new[] { "version", "--format", "{{.Server.Version}}" }, null, null,
                    TimeSpan.FromSeconds(5), CancellationToken.None)
                .GetAwaiter().GetResult();
            return !outcome.NotFound && outcome.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryRemoveContainerAsync(string containerName)
    {
        // Force-remove: kills the container (and its process tree) if still running, then deletes it.
        try
        {
            await SandboxProcess
                .RunAsync("docker", new[] { "rm", "--force", containerName }, null, null,
                    TimeSpan.FromSeconds(20), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* best effort — the container is disposable */ }
    }

    /// <summary>
    /// Copy the validated artifact bytes to a durable, cache-key-scoped host directory so the bridge can
    /// read them after the disposable staging dir is gone. Best-effort: a failure here just means the
    /// bridge can't map signals (it yields an empty manifest), so we return null rather than failing the
    /// whole run. This writes host data the untrusted container never had access to.
    /// </summary>
    private static string? TryPersistArtifact(string cacheKey, byte[] bytes)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DaxAlgoTerminal", "repro-artifacts", cacheKey);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ArtifactFileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteScratch(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort — staging is under temp and disposable */ }
    }

    private static string Tail(string s, int max = 400) =>
        s.Length <= max ? s : s[^max..];
}
