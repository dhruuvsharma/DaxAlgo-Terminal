using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Hosting;

namespace TradingTerminal.Infrastructure.Sidecar;

public static class SidecarServiceCollectionExtensions
{
    /// <summary>
    /// Registers the managed Python sidecar launcher: binds <see cref="SidecarOptions"/>, registers the
    /// <see cref="SidecarHostService"/> as a singleton, exposes it as <see cref="ISidecarController"/>
    /// (on-demand start from the UI) and as an <see cref="IHostedService"/> (auto-start on launch + kill
    /// on exit). Defaults degrade gracefully — with no sidecar present nothing starts and the app runs.
    /// </summary>
    public static IServiceCollection AddSidecar(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SidecarOptions>(configuration.GetSection(SidecarOptions.SectionName));

        services.AddSingleton<SidecarHostService>();
        services.AddSingleton<ISidecarController>(sp => sp.GetRequiredService<SidecarHostService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SidecarHostService>());
        return services;
    }
}
