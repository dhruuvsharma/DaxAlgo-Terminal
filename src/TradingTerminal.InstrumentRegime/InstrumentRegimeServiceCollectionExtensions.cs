using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Regime.Instrument;
using TradingTerminal.Infrastructure.Regime.Instrument;

namespace TradingTerminal.InstrumentRegime;

/// <summary>DI registration for the per-instrument regime tab, including the
/// <see cref="IInstrumentRegimeProvider"/> implementation from Infrastructure. Transient panel so
/// each open gets a fresh subscription that disposes with the tab.</summary>
public static class InstrumentRegimeServiceCollectionExtensions
{
    public static IServiceCollection AddInstrumentRegimeSurface(this IServiceCollection services)
    {
        services.AddSingleton<IInstrumentRegimeProvider, InstrumentRegimeService>();
        services.AddTransient<InstrumentRegimeViewModel>();
        services.AddTransient<InstrumentRegimeView>();
        return services;
    }
}
