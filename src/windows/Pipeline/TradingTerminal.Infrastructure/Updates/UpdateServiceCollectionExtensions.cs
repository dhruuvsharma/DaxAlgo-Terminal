using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Security;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Registers application update checking: a signature-verifying, offline-first
/// <see cref="HttpUpdateChecker"/> and the <see cref="UpdateCheckService"/> that schedules it.
///
/// With no <c>Updates:FeedUrl</c> or no <c>Updates:FeedPublicKey</c> the registration falls back to
/// <see cref="NullUpdateChecker"/> and no background service is added, so the app makes no network
/// call and the prompt can never appear. That is the shipped default.
/// </summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> used for the release manifest.</summary>
    public const string UpdateHttpClientName = "daxalgo-updates";

    /// <summary>Where the last verified manifest, its signature and the server ETag are cached.</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal", "updates");

    public static IServiceCollection AddUpdates(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(UpdatesOptions.SectionName).Get<UpdatesOptions>() ?? new UpdatesOptions();
        services.AddSingleton(options);

        var current = CurrentVersion();
        var verifier = new PinnedEcdsaVerifier(options.FeedPublicKey);

        if (string.IsNullOrWhiteSpace(options.FeedUrl) || !verifier.IsConfigured)
        {
            // Off: one code path for consumers, no network, no timer. The same object serves both
            // interfaces so the shell's resolve succeeds and the prompt simply never appears.
            var off = new NullUpdateChecker(current);
            services.AddSingleton<IUpdateChecker>(off);
            services.AddSingleton<IUpdateNotifier>(off);
            return services;
        }

        services.AddHttpClient(UpdateHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DaxAlgoTerminal/{current}");
        });

        services.AddSingleton<IUpdateChecker>(sp => new HttpUpdateChecker(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpdateHttpClientName),
            verifier,
            options.FeedUrl,
            current,
            DefaultCacheDirectory,
            sp.GetRequiredService<ILogger<HttpUpdateChecker>>()));

        // One instance behind three registrations: the host starts it, the shell observes it.
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UpdateCheckService>());
        services.AddSingleton<IUpdateNotifier>(sp => sp.GetRequiredService<UpdateCheckService>());
        return services;
    }

    /// <summary>
    /// The running version, preferring the informational version (which carries the real release
    /// string) and falling back to the assembly version. Unparsable ⇒ 0.0.0.0, which makes every
    /// published version look newer; that is the safe direction for a prompt-only feature.
    /// </summary>
    private static Version CurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any "+buildmetadata" or "-prerelease" so Version.TryParse can cope.
            var trimmed = informational.Split('+')[0].Split('-')[0];
            if (Version.TryParse(trimmed, out var fromInformational))
                return fromInformational;
        }
        return assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }
}
