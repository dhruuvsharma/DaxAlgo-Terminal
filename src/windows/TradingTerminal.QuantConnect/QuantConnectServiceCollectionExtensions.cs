using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.QuantConnect;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// DI registration for the QuantConnect / LEAN tool. Binds <see cref="LeanOptions"/>, seeds the shared
/// mutable <see cref="LeanRuntimeSettings"/>, and selects the <see cref="ILeanClient"/> implementation
/// by mode (local CLI today; the Cloud slot falls back to <see cref="NullLeanClient"/> until wired).
/// VM + window are transient so each open gets a fresh VM that disposes with the window. Called once
/// from <c>App.xaml.cs</c>.
/// </summary>
public static class QuantConnectServiceCollectionExtensions
{
    public static IServiceCollection AddQuantConnectSurface(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LeanOptions>(configuration.GetSection(LeanOptions.SectionName));

        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<LeanOptions>>().Value;
            return new LeanRuntimeSettings
            {
                Mode = o.Mode,
                CliPath = o.CliPath,
                ProjectsFolder = o.ProjectsFolder,
                DataFolder = o.DataFolder,
                RunTimeoutSeconds = o.RunTimeoutSeconds,
            };
        });

        services.AddSingleton<ILeanClient>(sp =>
        {
            var settings = sp.GetRequiredService<LeanRuntimeSettings>();
            return settings.Mode switch
            {
                LeanEngineMode.LocalCli => ActivatorUtilities.CreateInstance<LocalCliLeanClient>(sp),
                _ => new NullLeanClient(settings.Mode),
            };
        });

        services.AddTransient<QuantConnectViewModel>();
        services.AddTransient<QuantConnectWindow>();
        return services;
    }
}
