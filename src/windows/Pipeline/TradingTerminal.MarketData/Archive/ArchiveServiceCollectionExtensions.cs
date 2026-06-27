using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

namespace TradingTerminal.Infrastructure.MarketData.Archive;

public static class ArchiveServiceCollectionExtensions
{
    /// <summary>
    /// Registers the market-data offloader: bundle builder + manifest store + Telegram MTProto
    /// transport + scheduled archive service. Must be called after <c>AddMarketDataPipeline</c>
    /// because it depends on <see cref="Core.MarketData.IMarketDataStore"/> and
    /// <see cref="Core.MarketData.IInstrumentRegistry"/>. The UI must register an
    /// <see cref="ITelegramAuthPrompt"/> to drive the login dialog; this extension falls back to
    /// <see cref="NullTelegramAuthPrompt"/> if none is supplied (headless / CLI use).
    /// </summary>
    public static IServiceCollection AddMarketDataArchive(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArchiveOptions>(configuration.GetSection(ArchiveOptions.SectionName));
        services.Configure<TelegramArchiveOptions>(configuration.GetSection(TelegramArchiveOptions.SectionName));

        // Manifest store: small SQLite DB independent of the main store backend. Resolved as a
        // singleton so all consumers (archiver, UI activity tab) share one connection.
        services.AddSingleton(sp =>
        {
            var opts = (sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ArchiveOptions>>()).CurrentValue;
            // Treat blank as "not set": an empty DataSource makes SQLite hand every connection its own
            // private temp DB, so the table created on the write connection is invisible to readers
            // ("no such table: archive_manifest"). Fall back to the per-user default instead.
            var path = string.IsNullOrWhiteSpace(opts.ManifestDatabasePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DaxAlgoTerminal", "archive-manifest.db")
                : opts.ManifestDatabasePath;
            return new ArchiveManifestStore(path);
        });

        // Default prompt is a no-op; the App layer overrides it with the real UI bridge.
        services.AddSingleton<ITelegramAuthPrompt, NullTelegramAuthPrompt>();

        services.AddSingleton<TelegramArchiveTransport>();
        services.AddSingleton<IArchiveTransport>(sp => sp.GetRequiredService<TelegramArchiveTransport>());

        services.AddSingleton<IMarketDataArchiver, MarketDataArchiver>();

        // Schedule service runs all the time even when Enabled=false; it cheaply tick-checks and
        // returns early, so the cost of "off" is one Timer callback per 15 minutes.
        services.AddSingleton<ArchiveScheduleService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ArchiveScheduleService>());

        return services;
    }
}
