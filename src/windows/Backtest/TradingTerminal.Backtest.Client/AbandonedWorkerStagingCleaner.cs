using System.IO;
using System.Text.Json;
using TradingTerminal.Backtest.Protocol;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

/// <summary>
/// Removes only old, immediate worker-owned .staging-* directories beneath immediate job folders.
/// Completed artifacts, manifests, requests, job directories, and reparse points are never touched.
/// </summary>
internal static class AbandonedWorkerStagingCleaner
{
    public static int Cleanup(string jobRoot, TimeSpan minimumAge, DateTime utcNow)
    {
        if (minimumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        if (!Directory.Exists(jobRoot)) return 0;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobRoot));
        var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)
                             ?? throw new ArgumentException("The job root has no filesystem root.", nameof(jobRoot)));
        if (string.Equals(root, filesystemRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The job root cannot be a filesystem root.", nameof(jobRoot));
        var cutoffUtc = utcNow - minimumAge;
        var removed = 0;

        foreach (var rootStaging in Directory.EnumerateDirectories(
                     root,
                     ".staging-*",
                     SearchOption.TopDirectoryOnly))
        {
            if (TryDeleteOwnedStaging(root, rootStaging, cutoffUtc)) removed++;
        }

        foreach (var jobDirectory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsDirectChild(root, jobDirectory) || IsReparsePoint(jobDirectory)) continue;
            if (!HasOwnedRequestMarker(jobDirectory)) continue;

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateDirectories(jobDirectory, ".staging-*", SearchOption.TopDirectoryOnly)
                    .ToArray();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var candidate in candidates)
            {
                if (TryDeleteOwnedStaging(Path.GetFullPath(jobDirectory), candidate, cutoffUtc)) removed++;
            }
        }

        return removed;
    }

    private static bool TryDeleteOwnedStaging(string parent, string candidate, DateTime cutoffUtc)
    {
        try
        {
            if (!IsDirectChild(parent, candidate) || IsReparsePoint(candidate)) return false;
            if (!IsWorkerStagingName(Path.GetFileName(candidate))) return false;
            if (Directory.GetLastWriteTimeUtc(candidate) > cutoffUtc) return false;
            Directory.Delete(candidate, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsDirectChild(string parent, string candidate)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(
            Path.GetDirectoryName(fullCandidate),
            fullParent,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static bool IsWorkerStagingName(string name)
    {
        const string prefix = ".staging-";
        if (name.Length != prefix.Length + 32 || !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var character in name.AsSpan(prefix.Length))
        {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    private static bool HasOwnedRequestMarker(string jobDirectory)
    {
        var requestMarker = Path.Combine(jobDirectory, BacktestJobFiles.Request);
        if (!File.Exists(requestMarker) || IsReparsePoint(requestMarker)) return false;
        try
        {
            using var stream = new FileStream(
                requestMarker,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > BacktestProtocolLimits.MaxRequestBytes) return false;
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("protocol_version", out var protocol) &&
                   protocol.TryGetInt32(out var protocolVersion) &&
                   protocolVersion is >= 1 && protocolVersion <= BacktestProtocolVersions.Current &&
                   root.TryGetProperty("job_id", out var job) &&
                   job.ValueKind == JsonValueKind.String &&
                   string.Equals(job.GetString(), Path.GetFileName(jobDirectory), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
