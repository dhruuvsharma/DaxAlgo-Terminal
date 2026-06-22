using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Core.Research;

/// <summary>
/// The complete, deterministic description of a reproduction: which paper, which repo commit, and the
/// configuration knobs that select a minimal-vs-full run. The <see cref="CacheKey"/> is the identity
/// used by <c>IReproJobStore.FindCached</c> — two specs with the same arXiv id, repo commit, and
/// config produce the same key and therefore reuse a prior succeeded result instead of spawning a new
/// container.
/// </summary>
public sealed record ReproSpec(
    PaperRef Paper,
    RepoRef Repo,
    IReadOnlyDictionary<string, string> Config)
{
    // Unit separator (U+001F): delimits cache-key fields so no value can forge a field boundary.
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// Stable hash of (arXiv id, repo commit, config) — the reproduction cache key. Config entries are
    /// sorted so dictionary ordering can't change the key. Hex SHA-256 so it round-trips through SQLite
    /// as a plain text column.
    /// </summary>
    public string CacheKey
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Paper.ArxivId).Append(FieldSeparator);
            sb.Append(Repo.Commit).Append(FieldSeparator);
            foreach (var kvp in Config.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kvp.Key).Append(FieldSeparator).Append(kvp.Value).Append(FieldSeparator);

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    /// <summary>Convenience for a spec with no extra config.</summary>
    public static ReproSpec Minimal(PaperRef paper, RepoRef repo) =>
        new(paper, repo, new Dictionary<string, string>());
}
