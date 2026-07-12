namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>
/// Syncs the verified marketplace feed's <c>revoked[]</c> into the host's local kill-list
/// (<see cref="PluginRevocationList"/> / <c>revoked.json</c>) so the loader enforces distribution-channel
/// revocations on the next start — before any plugin code runs. This is the one place the "a plugin found
/// bad after the fact" signal crosses from the feed into the load path. Only call it with an index that has
/// already passed signature verification; a null / revocation-free index is a no-op.
/// </summary>
public static class PluginRevocationSync
{
    /// <summary>Merges the feed's revocations into <c>revoked.json</c> under <paramref name="pluginsRoot"/>.
    /// Returns the number of entries now on the local kill-list (0 when the feed had none). Best-effort —
    /// never throws.</summary>
    public static int Apply(string pluginsRoot, PluginIndex? index)
    {
        var feedRevocations = index?.Revoked;
        if (feedRevocations is not { Count: > 0 }) return 0;

        var mapped = feedRevocations.Select(r => new RevokedPlugin(r.Sha256, r.Id, r.Reason));
        return PluginRevocationList.Merge(pluginsRoot, mapped);
    }
}
