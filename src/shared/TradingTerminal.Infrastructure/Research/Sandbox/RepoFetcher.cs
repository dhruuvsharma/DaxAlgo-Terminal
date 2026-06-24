using System.IO;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research.Sandbox;

/// <summary>Result of a clone attempt: the scratch directory the repo was checked out into (when
/// successful) and an error message otherwise.</summary>
internal sealed record RepoFetchResult(bool Success, string? RepoDir, string? Error);

/// <summary>
/// Clones a <see cref="RepoRef"/> at its pinned commit into a fresh scratch directory using the trusted
/// <c>git</c> CLI (via <see cref="SandboxProcess"/>, kill-tree on timeout). The checkout becomes the
/// read-only payload the sandbox container runs; nothing executes on the host. The pinned commit is
/// mandatory — a moving HEAD would break the reproduction cache and determinism.
///
/// <para>The commit is validated against a strict hex-SHA shape before it reaches git, and all git
/// invocations pass arguments as a token list (no shell, no manual quoting) — defence-in-depth against
/// argument injection through a hostile <see cref="RepoRef.Commit"/>.</para>
/// </summary>
internal static class RepoFetcher
{
    /// <summary>A git object name: 7–40 lowercase hex characters. Anything else is rejected before git.</summary>
    private static readonly Regex CommitPattern = new("^[0-9a-f]{7,40}$", RegexOptions.Compiled);

    public static async Task<RepoFetchResult> FetchAsync(
        RepoRef repo,
        string scratchRoot,
        Action<string>? log,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repo.GitUrl))
            return new RepoFetchResult(false, null, "Repo git URL is empty.");
        if (string.IsNullOrWhiteSpace(repo.Commit))
            return new RepoFetchResult(false, null, "Repo commit pin is empty (required for determinism).");
        // Reject anything that isn't a plain hex object name. The ArgumentList conversion already removes
        // the injection vector; this rejects malformed/hostile pins outright (deny-by-default).
        if (!CommitPattern.IsMatch(repo.Commit))
            return new RepoFetchResult(false, null,
                "Repo commit pin must be a 7–40 char hex SHA (got an invalid value).");

        var repoDir = Path.Combine(scratchRoot, "repo");
        try
        {
            Directory.CreateDirectory(repoDir);
        }
        catch (Exception ex)
        {
            return new RepoFetchResult(false, null, $"Could not create scratch dir: {ex.Message}");
        }

        // Shallow clone then fetch+checkout the exact commit. --no-checkout keeps untrusted hooks from
        // running on clone; we check out the pinned tree explicitly.
        var clone = await SandboxProcess.RunAsync(
            "git",
            new[] { "clone", "--no-checkout", "--filter=blob:none", repo.GitUrl, repoDir },
            workingDirectory: scratchRoot,
            onLine: log,
            timeout: timeout,
            ct: ct).ConfigureAwait(false);

        if (clone.NotFound)
            return new RepoFetchResult(false, null, "git CLI not found on PATH.");
        if (!clone.Success)
            return new RepoFetchResult(false, null, $"git clone failed: {Tail(clone.Output)}");

        var checkout = await SandboxProcess.RunAsync(
            "git",
            new[] { "-c", "core.hooksPath=/dev/null", "checkout", repo.Commit },
            workingDirectory: repoDir,
            onLine: log,
            timeout: timeout,
            ct: ct).ConfigureAwait(false);

        if (!checkout.Success)
            return new RepoFetchResult(false, null, $"git checkout {repo.Commit} failed: {Tail(checkout.Output)}");

        return new RepoFetchResult(true, repoDir, null);
    }

    private static string Tail(string s, int max = 400) =>
        s.Length <= max ? s : s[^max..];
}
