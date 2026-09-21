using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Security;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Registers application updating: a signature-verifying, offline-first <see cref="HttpUpdateChecker"/>,
/// the <see cref="UpdateCheckService"/> that schedules it, and — when installing is permitted — the
/// <see cref="HttpUpdateDownloader"/> and <see cref="SilentSetupUpdateInstaller"/> that act on what it
/// finds.
///
/// <para>With no <c>Updates:FeedUrl</c> or no <c>Updates:FeedPublicKey</c> the whole feature falls back
/// to <see cref="NullUpdateChecker"/> and <see cref="NullUpdateInstaller"/>, no background service is
/// added, the app makes no network call and the prompt can never appear. That is the shipped default.</para>
///
/// <para>Installing is gated <i>again</i>, behind the check: it also needs
/// <c>Updates:AllowAutomaticInstall</c> and a running version the build can actually name. Detection
/// stays on when installing is off, so the banner keeps working as a link to the release notes.</para>
/// </summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> used for the release manifest.</summary>
    public const string UpdateHttpClientName = "daxalgo-updates";

    /// <summary>Named <see cref="HttpClient"/> used for the installer download. Separate from the
    /// manifest client because a few hundred megabytes cannot share a 15-second timeout.</summary>
    public const string InstallerHttpClientName = "daxalgo-updates-installer";

    /// <summary>Where the last verified manifest, its signature and the server ETag are cached.</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal", "updates");

    /// <summary>Where a downloaded installer waits between verification and launch.</summary>
    public static string DefaultStagingDirectory => Path.Combine(DefaultCacheDirectory, "staging");

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
            AddInertInstalling(services, "No update feed is configured.");
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

        AddInstalling(services, options, current);
        return services;
    }

    /// <summary>
    /// The whole policy on whether this build may install an update, in one place so it can be read
    /// and tested without standing a container up. Returns null when installing is permitted, or the
    /// reason it is not.
    ///
    /// <para>This deliberately says nothing about whether a feed is configured — that gate sits above
    /// it, in <see cref="AddUpdates"/>. Here we only answer "given that we can detect updates, may we
    /// act on one?".</para>
    /// </summary>
    public static string? InstallUnavailableReason(UpdatesOptions options, Version current)
    {
        if (!options.AllowAutomaticInstall)
            return "Updates:AllowAutomaticInstall is false.";

        // CurrentVersion() falls back to 0.0.0.0 when the assembly carries no usable version. For a
        // prompt that is the safe direction — every release looks newer, so the user is told. For an
        // INSTALLER it is exactly backwards: the app would fetch and run a setup on every launch,
        // forever, and would just as happily "upgrade" to what it is already running, or to something
        // older. A build that cannot name its own version has no business deciding it is out of date.
        if (current == new Version(0, 0, 0, 0))
            return "This build reports no version, so it cannot tell whether an installer would upgrade it.";

        // The whole install path is Authenticode plus an Inno setup executable.
        if (!OperatingSystem.IsWindows())
            return "Automatic installing is Windows-only.";

        return null;
    }

    /// <summary>
    /// Registers the download-and-install half, or an inert stand-in with the reason recorded.
    /// </summary>
    private static void AddInstalling(IServiceCollection services, UpdatesOptions options, Version current)
    {
        if (InstallUnavailableReason(options, current) is { } reason)
        {
            AddInertInstalling(services, reason);
            return;
        }

        services.AddHttpClient(InstallerHttpClientName, client =>
        {
            // No overall timeout: the ceiling on a download is UpdatesOptions.MaxInstallerBytes plus
            // the user's own cancellation, not a stopwatch that would kill a large release on a slow
            // line halfway through. HttpClient's default 100s would do exactly that.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DaxAlgoTerminal/{current}");
        });

        services.AddSingleton<IUpdateDownloader>(sp => new HttpUpdateDownloader(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(InstallerHttpClientName),
            SignatureInspector(),
            DefaultStagingDirectory,
            allowed: true,
            options.InstallerCertificateThumbprint,
            options.MaxInstallerBytes,
            sp.GetRequiredService<ILogger<HttpUpdateDownloader>>()));

        services.AddSingleton<IUpdateInstaller>(sp => new SilentSetupUpdateInstaller(
            SignatureInspector(),
            allowed: true,
            options.InstallerCertificateThumbprint,
            sp.GetRequiredService<ILogger<SilentSetupUpdateInstaller>>()));
    }

    /// <summary>One object for both seams, so a shell resolving either gets the same "no" and the
    /// banner degrades to its link-only shape.</summary>
    private static void AddInertInstalling(IServiceCollection services, string reason)
    {
        var off = new NullUpdateInstaller(reason);
        services.AddSingleton<IUpdateDownloader>(off);
        services.AddSingleton<IUpdateInstaller>(off);
    }

    /// <summary>The production Authenticode inspector on Windows; the always-unsigned one elsewhere,
    /// which combined with the mandatory signature check rejects everything.</summary>
    private static IPluginSignatureInspector SignatureInspector() =>
        OperatingSystem.IsWindows() ? new AuthenticodeSignatureInspector() : new NullSignatureInspector();

    /// <summary>
    /// The running version, preferring the informational version (which carries the real release
    /// string) and falling back to the assembly version. Unparsable ⇒ 0.0.0.0, which makes every
    /// published version look newer; that is the safe direction for a prompt, and is why
    /// <see cref="AddInstalling"/> refuses to install from such a build.
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
