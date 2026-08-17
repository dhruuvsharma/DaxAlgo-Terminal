using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The composition contract. A green build says nothing about whether the container can actually
/// hand out these services, and the "off unless configured" default is a security property, not a
/// convenience — so both halves are asserted against a real container.
/// </summary>
public sealed class AddUpdatesTests
{
    // Any syntactically valid P-256 SubjectPublicKeyInfo; registration only checks that one is present.
    private const string SomeKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEQTUiW5czw2h70HjeT/JADf0IbFCWFhCKMuLX8Kw+ko2nk2Z2pphSY5dXbiTC4hVQ2SqTIFyWxVhFP5kjmR1IwQ==";

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
    public void Registers_the_inert_checker_when_nothing_is_configured()
    {
        using var provider = Build();

        provider.GetRequiredService<IUpdateChecker>().Should().BeOfType<NullUpdateChecker>();
        provider.GetRequiredService<IUpdateNotifier>().Should().BeOfType<NullUpdateChecker>();

        // No timer, so a default install never wakes up to talk to a release host.
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://releases.example.com/release.json", "")]  // url but no key
    [InlineData("", SomeKey)]                                       // key but no url
    public void Stays_off_unless_BOTH_the_url_and_the_pinned_key_are_present(string url, string key)
    {
        using var provider = Build(
            ("Updates:FeedUrl", url),
            ("Updates:FeedPublicKey", key));

        provider.GetRequiredService<IUpdateChecker>().Should().BeOfType<NullUpdateChecker>(
            "a feed with no pinned key could serve any manifest it liked");
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_NotConfigured_from_the_inert_checker_without_touching_the_network()
    {
        using var provider = Build();

        var result = await provider.GetRequiredService<IUpdateChecker>().CheckAsync();

        result.Outcome.Should().Be(UpdateOutcome.NotConfigured);
    }

    [Fact]
    public void Registers_the_real_checker_and_scheduler_once_both_are_present()
    {
        using var provider = Build(
            ("Updates:FeedUrl", "https://releases.example.com/release.json"),
            ("Updates:FeedPublicKey", SomeKey));

        provider.GetRequiredService<IUpdateChecker>().Should().BeOfType<HttpUpdateChecker>();
        provider.GetServices<IHostedService>().Should().ContainSingle()
            .Which.Should().BeOfType<UpdateCheckService>();
    }

    [Fact]
    public void Shares_one_instance_between_the_hosted_service_and_the_notifier()
    {
        // Two instances would mean the timer runs on one object and the UI listens to another —
        // the banner would then never appear, and only at runtime.
        using var provider = Build(
            ("Updates:FeedUrl", "https://releases.example.com/release.json"),
            ("Updates:FeedPublicKey", SomeKey));

        provider.GetRequiredService<IUpdateNotifier>()
            .Should().BeSameAs(provider.GetServices<IHostedService>().Single());
    }
}
