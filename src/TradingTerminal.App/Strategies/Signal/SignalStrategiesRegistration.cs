using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Backtest;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;

namespace TradingTerminal.App.Strategies.Signal;

/// <summary>
/// Registers every entry in <see cref="BacktestStrategyCatalog"/> as a live signal-mode
/// strategy: one <see cref="ITradingStrategy"/> descriptor (so it appears in the left
/// Strategies pane) and one <see cref="StrategyFactoryRegistration"/> (so opening it
/// builds a <see cref="LiveSignalStrategyViewModel"/> configured for that strategy plus a
/// shared <see cref="LiveSignalStrategyView"/>).
///
/// Strategy ids are prefixed with <c>signal.</c> so they don't collide with the
/// dedicated live strategies (<c>rsi.overbought.oversold</c>, <c>cumulative-delta</c>).
/// </summary>
public static class SignalStrategiesRegistration
{
    public const string IdPrefix = "signal.";

    public static IServiceCollection AddSignalGeneratorStrategies(this IServiceCollection services)
    {
        services.AddTransient<LiveSignalStrategyView>();
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
                ViewFactory: sp => sp.GetRequiredService<LiveSignalStrategyView>(),
                ViewModelFactory: sp => new LiveSignalStrategyViewModel(
                    captured,
                    sp.GetRequiredService<IMarketDataRepository>(),
                    sp.GetRequiredService<INotificationPublisher>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<ISignalGeneratorRouterFactory>(),
                    sp.GetRequiredService<ILogger<LiveSignalStrategyViewModel>>())));
        }

        return services;
    }
}
