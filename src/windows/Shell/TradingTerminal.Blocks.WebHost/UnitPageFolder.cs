using System.IO;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// Puts a unit's page files on disk, where WebView2 can serve them.
///
/// <para>Written to a fresh folder and swapped in, never overwritten in place: a window already showing
/// the previous version holds its files open, and a page half old and half new is a page that throws
/// for reasons nobody can reproduce. When the old folder cannot be replaced because it is in use, the
/// new one is served from beside it.</para>
/// </summary>
public static class UnitPageFolder
{
    /// <summary><c>%LocalAppData%\DaxAlgo Terminal\units</c>.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgo Terminal", "units");

    /// <summary>Writes <paramref name="pageFiles"/> (named <c>ui/…</c>) and returns the folder to serve.</summary>
    public static string Write(string root, string unitId, IReadOnlyList<StrategyFile> pageFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(pageFiles);

        var unitFolder = Path.Combine(root, Safe(unitId));
        Directory.CreateDirectory(unitFolder);
        PruneStaging(unitFolder);

        var staging = Path.Combine(unitFolder, $"ui.{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        foreach (var file in pageFiles)
        {
            var relative = (file.Name ?? string.Empty).Replace('\\', '/');
            if (relative.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) relative = relative[3..];

            var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (relative.Length == 0 || !target.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{file.Name}' is not a page file path under ui/.", nameof(pageFiles));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, file.Content ?? string.Empty);
        }

        var current = Path.Combine(unitFolder, "ui");
        try
        {
            if (Directory.Exists(current)) Directory.Delete(current, recursive: true);
            Directory.Move(staging, current);
            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An open window is serving the old folder. The new page is served from staging instead,
            // and cleared the next time this unit's page is written.
            return staging;
        }
    }

    private static void PruneStaging(string unitFolder)
    {
        foreach (var stale in Directory.EnumerateDirectories(unitFolder, "ui.*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddHours(-1))
                    Directory.Delete(stale, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use; the next write tries again.
            }
        }
    }

    internal static string Safe(string unitId)
    {
        var safe = new string(unitId.Trim().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.');
        return safe.Length == 0 ? "unit" : safe;
    }
}
