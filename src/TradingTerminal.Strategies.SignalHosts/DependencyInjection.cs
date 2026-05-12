using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.Strategies.SignalHosts;

/// <summary>
/// Registers every entry in <see cref="BacktestStrategyCatalog"/> as a live signal-mode
/// strategy: one <see cref="ITradingStrategy"/> descriptor (so it appears in the left
/// Strategies pane) plus a <see cref="StrategyFactoryRegistration"/> that builds a
/// <see cref="LiveSignalStrategyWindow"/> per open — same pattern as RSI / Cumulative
/// Delta, so each strategy opens as its own MetroWindow rather than as a docked tab.
///
/// Strategy ids are prefixed with <c>signal.</c> so they don't collide with the dedicated
/// live strategies (<c>rsi.overbought.oversold</c>, <c>cumulative-delta</c>).
/// </summary>
public static class DependencyInjection
{
    public const string IdPrefix = "signal.";

    public static IServiceCollection AddSignalHostStrategies(this IServiceCollection services)
    {
        services.AddTransient<LiveSignalStrategyWindow>();
        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();

        foreach (var option in BacktestStrategyCatalog.All)
        {
            var captured = option;
            var liveId = IdPrefix + captured.Id;

            services.AddSingleton<ITradingStrategy>(_ =>
                new LiveSignalStrategy(
                    id: liveId,
                    displayName: captured.DisplayName,
                    description: $"Signal-mode wrapper around the {captured.DisplayName} backtest strategy. Pick an instrument, press Start, and every order the strategy would submit fires as a notification."));

            services.AddSingleton(new StrategyFactoryRegistration(
                StrategyId: liveId,
                ViewFactory: sp => sp.GetRequiredService<LiveSignalStrategyWindow>(),
                ViewModelFactory: sp =>
                {
                    // Per-strategy logger category so log filtering by id is possible
                    // (otherwise all 22 hosts would log under "LiveSignalStrategyViewModel").
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var logger = loggerFactory.CreateLogger($"LiveSignalStrategy.{captured.Id}");
                    return new LiveSignalStrategyViewModel(
                        captured,
                        sp.GetRequiredService<IMarketDataRepository>(),
                        sp.GetRequiredService<INotificationPublisher>(),
                        sp.GetRequiredService<IClock>(),
                        sp.GetRequiredService<ISignalGeneratorRouterFactory>(),
                        logger);
                }));
        }

        return services;
    }
}
