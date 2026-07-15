using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// The WPF <see cref="IAuthoredStrategyViewComposer"/>: hands out a <see cref="ComposedStrategyView"/>
/// built from the descriptor's <see cref="ITradingStrategy.DataRequirement"/>. Registered by the shells
/// (all three editions); resolved lazily by the authored-strategy installer and by the SDK's plugin
/// bootstrap on restart, both of which fall back to "backtest-only" when no composer is present.
/// </summary>
public sealed class AuthoredStrategyViewComposer(IServiceProvider services) : IAuthoredStrategyViewComposer
{
    /// <summary>Must run on the UI thread — it builds WPF controls. Both call sites (the strategy
    /// factory's ViewFactory and the installer's registration) already do.</summary>
    public object ComposeView(ITradingStrategy descriptor) => new ComposedStrategyView(descriptor, services);
}

/// <summary>DI registration for the composed default strategy window.</summary>
public static class StrategyComposerServiceCollectionExtensions
{
    /// <summary>Lets this shell open authored strategies that shipped no view of their own: the host
    /// composes the live window from the strategy's declared data requirement (depth → order-book
    /// ladder, trade tape → footprint, bars → chart), all panels in their Embedded (ML-off) presets.</summary>
    public static IServiceCollection AddStrategyViewComposer(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthoredStrategyViewComposer, AuthoredStrategyViewComposer>();
        return services;
    }
}
