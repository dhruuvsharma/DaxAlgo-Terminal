using System.IO;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>Where a feed-listed plugin stands relative to what's installed locally.</summary>
public enum PluginInstallState
{
    /// <summary>Nothing with this plugin id is installed.</summary>
    NotInstalled,

    /// <summary>Installed at (or above) the feed's latest version.</summary>
    UpToDate,

    /// <summary>Installed at an older version than the feed offers.</summary>
    UpdateAvailable,
}

/// <summary>
/// One browsable catalog card: a verified feed entry joined to local install state and the feed's own
/// revocation view. It's a pure projection — computed from (verified index + installed manifests), doing
/// no I/O beyond reading the plugin folders. The UI binds directly to this.
/// </summary>
public sealed record PluginCatalogItem(
    PluginFeedEntry Entry,
    PluginInstallState State,
    string? InstalledVersion,
    bool Revoked,
    string? RevokedReason)
{
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string Description => Entry.Description;
    public string LatestVersion => Entry.Latest.Version;
    public PluginFeedVersion Latest => Entry.Latest;
    public IReadOnlyList<string> Tags => Entry.Tags ?? [];
    public string? PaperUrl => Entry.PaperUrl;

    /// <summary>A fresh install is offered only when nothing is installed and the build isn't revoked.</summary>
    public bool CanInstall => State == PluginInstallState.NotInstalled && !Revoked;

    /// <summary>An update is offered only when an older build is installed and the new one isn't revoked.</summary>
    public bool CanUpdate => State == PluginInstallState.UpdateAvailable && !Revoked;

    /// <summary>Short human-readable status the catalog card shows (Available / Installed / Update / Revoked).</summary>
    public string StateLabel => Revoked
        ? "Revoked"
        : State switch
        {
            PluginInstallState.NotInstalled => $"Available · v{LatestVersion}",
            PluginInstallState.UpToDate => $"Installed · v{InstalledVersion}",
            PluginInstallState.UpdateAvailable => $"Update available · v{InstalledVersion} → v{LatestVersion}",
            _ => string.Empty,
        };
}

/// <summary>
/// Builds the marketplace catalog: joins the verified feed index to what's installed on disk so the UI can
/// show Install / Update / Installed and grey out revoked builds. Reading installed manifests never throws
/// (a broken or absent manifest just means "unknown installed version"), and the whole thing is a pure
/// function of its inputs — trivially testable and safe to call on any thread.
/// </summary>
public static class PluginCatalog
{
    /// <summary>Projects every feed entry into a catalog row carrying its local install state and whether
    /// the feed has revoked it. A null / empty index yields an empty catalog (feed off / not yet fetched).</summary>
    public static IReadOnlyList<PluginCatalogItem> Build(PluginIndex? index, string pluginsRoot)
    {
        if (index?.Plugins is not { Count: > 0 }) return [];

        var installed = ReadInstalledVersions(pluginsRoot);
        var revoked = index.Revoked ?? [];

        var items = new List<PluginCatalogItem>(index.Plugins.Count);
        foreach (var entry in index.Plugins)
        {
            installed.TryGetValue(entry.Id, out var installedVersion);
            var state = installedVersion is null
                ? PluginInstallState.NotInstalled
                : IsOlder(installedVersion, entry.Latest.Version)
                    ? PluginInstallState.UpdateAvailable
                    : PluginInstallState.UpToDate;

            var isRevoked = IsRevoked(revoked, entry, out var reason);
            items.Add(new PluginCatalogItem(entry, state, installedVersion, isRevoked, reason));
        }
        return items;
    }

    /// <summary>Case-insensitive substring filter over name / id / publisher / description / tags. A blank
    /// query returns the list unchanged.</summary>
    public static IReadOnlyList<PluginCatalogItem> Search(IReadOnlyList<PluginCatalogItem> items, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return items;
        var q = query.Trim();
        bool Has(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        return items
            .Where(i => Has(i.Name) || Has(i.Id) || Has(i.Publisher) || Has(i.Description) || i.Tags.Any(Has))
            .ToList();
    }

    /// <summary>All rows that currently offer an update — the source for "Update all".</summary>
    public static IReadOnlyList<PluginCatalogItem> Updatable(IReadOnlyList<PluginCatalogItem> items) =>
        items.Where(i => i.CanUpdate).ToList();

    private static Dictionary<string, string> ReadInstalledVersions(string pluginsRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pluginsRoot)) return map;

        foreach (var dir in Directory.EnumerateDirectories(pluginsRoot))
        {
            PluginManifest? manifest;
            try { manifest = PluginManifest.TryRead(dir); }
            catch { continue; } // a broken manifest isn't the catalog's problem — the loader reports it
            if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                map[manifest.Id] = manifest.Version;
        }
        return map;
    }

    private static bool IsRevoked(IReadOnlyList<PluginFeedRevocation> revoked, PluginFeedEntry entry, out string? reason)
    {
        foreach (var r in revoked)
        {
            var idMatch = !string.IsNullOrWhiteSpace(r.Id)
                && string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase);
            var hashMatch = !string.IsNullOrWhiteSpace(r.Sha256)
                && string.Equals(r.Sha256, entry.Latest.Sha256, StringComparison.OrdinalIgnoreCase);
            if (idMatch || hashMatch)
            {
                reason = string.IsNullOrWhiteSpace(r.Reason) ? "This build has been revoked." : r.Reason;
                return true;
            }
        }
        reason = null;
        return false;
    }

    /// <summary>True when <paramref name="installed"/> is a lower version than <paramref name="latest"/>,
    /// comparing the release core (ignoring any prerelease/build tag) — the same semantics the installer
    /// uses to describe an update vs a downgrade.</summary>
    private static bool IsOlder(string installed, string latest)
    {
        static string Core(string v) => v.Split('-', '+')[0];
        return Version.TryParse(Core(installed), out var iv)
            && Version.TryParse(Core(latest), out var lv)
            && iv < lv;
    }
}
