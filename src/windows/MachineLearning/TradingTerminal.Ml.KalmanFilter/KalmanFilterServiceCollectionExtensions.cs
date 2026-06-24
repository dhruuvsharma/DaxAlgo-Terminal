using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ml.KalmanFilter;

/// <summary>DI registration for the Kalman Filter tool (Machine Learning menu) — local-level /
/// local-linear-trend smoothing and the dynamic pairs hedge ratio (Core math), offline analysis
/// over historical bars. Transient so each open gets a fresh VM.</summary>
public static class KalmanFilterServiceCollectionExtensions
{
    public static IServiceCollection AddKalmanFilterSurface(this IServiceCollection services)
    {
        services.AddTransient<KalmanFilterViewModel>();
        services.AddTransient<KalmanFilterView>();
        return services;
    }
}
