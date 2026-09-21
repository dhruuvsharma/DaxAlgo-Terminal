using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// Composition for the install half. Installing is gated twice — once by the feed being configured at
/// all, and again by <see cref="UpdateServiceCollectionExtensions.InstallUnavailableReason"/> — and
/// both gates are security properties rather than conveniences, so both are asserted against a real
/// container.
/// </summary>
public sealed class AddUpdatesInstallingTests
{
    // Any syntactically valid P-256 SubjectPublicKeyInfo; registration only checks that one is present.
    private const string SomeKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQTUiW5czw2h70HjeT/JADf0IbFCWFhCKMuLX8Kw+ko2nk2Z2pphSY5dXbiTC4hVQ2SqTIFyWxVhFP5kjmR1IwQ==";

    private const string SomeFeed = "https://releases.example.com/release.json";

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUpdates(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void An_unconfigured_build_resolves_both_install_seams_and_both_say_no()
    {
        // The shell resolves these unconditionally, so "off" must still be resolvable — the same
        // reason NullUpdateChecker doubles as the off-state notifier.
        using var provider = Build();

        provider.GetRequiredService<IUpdateDownloader>().Should().BeOfType<NullUpdateInstaller>();
        provider.GetRequiredService<IUpdateInstaller>().Should().BeOfType<NullUpdateInstaller>();
        provider.GetRequiredService<IUpdateDownloader>().CanInstall.Should().BeFalse();
        provider.GetRequiredService<IUpdateInstaller>().CanInstall.Should().BeFalse();
    }

    [Fact]
    public void A_configured_feed_registers_the_real_downloader_and_installer()
    {
        using var provider = Build(
            ("Updates:FeedUrl", SomeFeed),
            ("Updates:FeedPublicKey", SomeKey));

        provider.GetRequiredService<IUpdateDownloader>().Should().BeOfType<HttpUpdateDownloader>();
        provider.GetRequiredService<IUpdateInstaller>().Should().BeOfType<SilentSetupUpdateInstaller>();
    }

    [Fact]
    public void Checking_survives_when_installing_is_switched_off()
    {
        // Turning installing off must leave the banner working as a link, not disable updates wholesale.
        using var provider = Build(
            ("Updates:FeedUrl", SomeFeed),
            ("Updates:FeedPublicKey", SomeKey),
            ("Updates:AllowAutomaticInstall", "false"));

        provider.GetRequiredService<IUpdateChecker>().Should().BeOfType<HttpUpdateChecker>();
        provider.GetRequiredService<IUpdateDownloader>().Should().BeOfType<NullUpdateInstaller>();
        provider.GetRequiredService<IUpdateInstaller>().Should().BeOfType<NullUpdateInstaller>();
    }

    [Fact]
    public void Installing_cannot_be_switched_on_without_a_feed()
    {
        // AllowAutomaticInstall only ever narrows. Without a pinned key there is no signed manifest,
        // so there is no trustworthy hash, so there is nothing safe to install.
        using var provider = Build(("Updates:AllowAutomaticInstall", "true"));

        provider.GetRequiredService<IUpdateDownloader>().CanInstall.Should().BeFalse();
    }

    [Fact]
    public void The_configured_options_reach_the_container()
    {
        using var provider = Build(
            ("Updates:FeedUrl", SomeFeed),
            ("Updates:FeedPublicKey", SomeKey),
            ("Updates:InstallerCertificateThumbprint", "AABBCC"),
            ("Updates:MaxInstallerBytes", "1048576"));

        var options = provider.GetRequiredService<UpdatesOptions>();
        options.InstallerCertificateThumbprint.Should().Be("AABBCC");
        options.MaxInstallerBytes.Should().Be(1048576);
        options.AllowAutomaticInstall.Should().BeTrue("installing is on by default once a feed is pinned");
    }

    // ── the policy itself ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_normal_Windows_build_with_a_known_version_may_install()
    {
        UpdateServiceCollectionExtensions
            .InstallUnavailableReason(new UpdatesOptions(), new Version(1, 3, 2))
            .Should().BeNull();
    }

    [Fact]
    public void A_build_that_cannot_name_its_own_version_may_never_install()
    {
        // CurrentVersion() answers 0.0.0.0 when the assembly carries no usable version. Every release
        // then looks newer — fine for a prompt, catastrophic for an installer, which would download
        // and run a setup on every single launch and could "upgrade" backwards.
        UpdateServiceCollectionExtensions
            .InstallUnavailableReason(new UpdatesOptions(), new Version(0, 0, 0, 0))
            .Should().NotBeNull();
    }

    [Fact]
    public void Switching_automatic_installing_off_is_honoured_by_the_policy()
    {
        UpdateServiceCollectionExtensions
            .InstallUnavailableReason(new UpdatesOptions { AllowAutomaticInstall = false }, new Version(1, 3, 2))
            .Should().NotBeNull();
    }
}
