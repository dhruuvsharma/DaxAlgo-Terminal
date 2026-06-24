using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Regime;

namespace TradingTerminal.Infrastructure.Regime;

public static class RegimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Market Regime composite: the four data clients (each on its own named
    /// HttpClient with the headers that endpoint needs), the orchestrating provider, the
    /// background refresh/notify loop, and the risk-off signal gate (which supersedes the
    /// notifications module's no-op default). Call this <em>after</em> <c>AddNotifications</c>
    /// so the regime gate wins the <see cref="ISignalGate"/> resolution.
    /// </summary>
    public static IServiceCollection AddMarketRegime(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MarketRegimeOptions>(configuration.GetSection(MarketRegimeOptions.SectionName));

        const string chromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                                + "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";

        services.AddHttpClient(YahooChartClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(chromeUa);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddHttpClient(FredClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient(CnnFearGreedClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            // CNN bot-blocks a Windows UA on this endpoint (HTTP 418); a Mac UA + Referer works.
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            c.DefaultRequestHeaders.Referrer = new Uri("https://www.cnn.com/markets/fear-and-greed");
        });

        services.AddHttpClient(AaiiSentimentClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(8);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(chromeUa);
            c.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        });

        services.AddSingleton<YahooChartClient>();
        services.AddSingleton<FredClient>();
        services.AddSingleton<CnnFearGreedClient>();
        services.AddSingleton<AaiiSentimentClient>();

        services.AddSingleton<MarketRegimeService>();
        services.AddSingleton<IMarketRegimeProvider>(sp => sp.GetRequiredService<MarketRegimeService>());

        services.AddSingleton<IHostedService, RegimeRefreshLoop>();

        // Supersedes AllowAllSignalGate registered by AddNotifications (last registration wins).
        services.AddSingleton<ISignalGate, RegimeSignalGate>();

        return services;
    }
}
