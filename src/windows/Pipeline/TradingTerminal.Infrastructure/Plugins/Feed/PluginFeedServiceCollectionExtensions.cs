using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

public static class PluginFeedServiceCollectionExtensions
{
    /// <summary>
    /// Registers the marketplace feed subsystem: an <see cref="IHttpClientFactory"/>, a background-refreshing
    /// signature-verifying <see cref="PluginFeedClient"/> (offline-first, last-good cached under LocalAppData),
    /// and a hosted service that refreshes on launch and syncs revocations. Call it from a shell's
    /// <c>AddStrategyPlugins</c> AFTER <see cref="PluginHostContext"/> is registered (the hosted service reads
    /// its plugins root). With no <see cref="PluginsOptions.FeedUrl"/> / <see cref="PluginsOptions.FeedPublicKey"/>
    /// configured the client reports not-configured and the catalog tab is simply empty — nothing fails.
    /// </summary>
    public static IServiceCollection AddPluginFeed(this IServiceCollection services, PluginsOptions options)
    {
        services.AddHttpClient();

        services.AddSingleton(sp => new PluginFeedClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(FeedHttpClientName),
            new FeedSignatureVerifier(options.FeedPublicKey),
            options.FeedUrl,
            FeedCacheDirectory(),
            sp.GetService<ILogger<PluginFeedClient>>()));

        services.AddSingleton<IHostedService>(sp => new PluginFeedRefreshService(
            sp.GetRequiredService<PluginFeedClient>(),
            sp.GetRequiredService<PluginHostContext>(),
            sp.GetService<ILogger<PluginFeedRefreshService>>()));

        return services;
    }

    /// <summary>Named <see cref="HttpClient"/> the feed client and catalog downloads share.</summary>
    public const string FeedHttpClientName = "daxalgo-plugin-feed";

    /// <summary>Where the last-good feed index + signature + etag are cached (survives restarts).</summary>
    public static string FeedCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal", "plugin-feed");
}
