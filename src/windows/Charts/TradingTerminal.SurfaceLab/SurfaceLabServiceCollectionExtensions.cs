using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.SurfaceLab;

/// <summary>DI registration for the 3D Surface Lab (Charts menu) — parameter-optimization
/// landscapes, seasonality surfaces, and cross-sectional conditional-return surfaces over
/// historical bars. Transient so each open gets a fresh VM.</summary>
public static class SurfaceLabServiceCollectionExtensions
{
    public static IServiceCollection AddSurfaceLabSurface(this IServiceCollection services)
    {
        services.AddTransient<SurfaceLabViewModel>();
        services.AddTransient<SurfaceLabView>();
        return services;
    }
}
