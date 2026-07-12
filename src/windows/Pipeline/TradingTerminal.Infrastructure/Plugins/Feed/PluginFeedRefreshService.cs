using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>
/// On launch, refreshes the marketplace feed in the background (fire-and-forget — it NEVER blocks or fails
/// startup) and syncs the feed's revocations into the local kill-list, so a build withdrawn upstream is
/// refused on the next start even if the user never opens the Plugin Manager. The catalog UI also refreshes
/// on demand; this is the belt-and-braces path for the security-relevant revocation sync. With no feed
/// configured it does nothing.
/// </summary>
public sealed class PluginFeedRefreshService : IHostedService
{
    private readonly PluginFeedClient _feed;
    private readonly string _pluginsRoot;
    private readonly ILogger? _logger;

    public PluginFeedRefreshService(PluginFeedClient feed, PluginHostContext host, ILogger? logger = null)
    {
        _feed = feed;
        _pluginsRoot = host.PluginsRoot;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_feed.IsConfigured)
            _ = Task.Run(RefreshAndSyncAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RefreshAndSyncAsync()
    {
        try
        {
            var result = await _feed.RefreshAsync().ConfigureAwait(false);
            if (result.Index is { } index)
            {
                var revoked = PluginRevocationSync.Apply(_pluginsRoot, index);
                if (revoked > 0)
                    _logger?.LogInformation("Plugin feed: synced {Count} revocation(s) to the local kill-list.", revoked);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Background plugin-feed refresh failed (ignored).");
        }
    }
}
