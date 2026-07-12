using System.IO;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// The catalog projection (issue #25): a verified feed index joined to what's installed on disk becomes
/// Install / Update / Installed rows, revoked builds are flagged and can't be installed, search filters,
/// and the feed's revocations sync into the local kill-list the loader enforces.
/// </summary>
public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _pluginsRoot =
        Path.Combine(Path.GetTempPath(), "daxalgo-tests", "catalog-" + Guid.NewGuid().ToString("N"));

    public PluginCatalogTests() => Directory.CreateDirectory(_pluginsRoot);

    public void Dispose()
    {
        try { Directory.Delete(_pluginsRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── catalog build ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_feed_entry_with_nothing_installed_is_offered_for_install()
    {
        var catalog = PluginCatalog.Build(Index(Entry("a.plugin", "1.2.0")), _pluginsRoot);

        var row = catalog.Should().ContainSingle().Subject;
        row.State.Should().Be(PluginInstallState.NotInstalled);
        row.InstalledVersion.Should().BeNull();
        row.CanInstall.Should().BeTrue();
        row.CanUpdate.Should().BeFalse();
    }

    [Fact]
    public void An_older_installed_version_offers_an_update()
    {
        InstallManifest("a.plugin", "1.0.0");

        var row = PluginCatalog.Build(Index(Entry("a.plugin", "1.2.0")), _pluginsRoot).Single();

        row.State.Should().Be(PluginInstallState.UpdateAvailable);
        row.InstalledVersion.Should().Be("1.0.0");
        row.CanUpdate.Should().BeTrue();
        row.CanInstall.Should().BeFalse();
    }

    [Fact]
    public void The_current_version_shows_as_up_to_date()
    {
        InstallManifest("a.plugin", "1.2.0");

        var row = PluginCatalog.Build(Index(Entry("a.plugin", "1.2.0")), _pluginsRoot).Single();

        row.State.Should().Be(PluginInstallState.UpToDate);
        row.CanUpdate.Should().BeFalse();
        row.CanInstall.Should().BeFalse();
    }

    [Fact]
    public void Prerelease_core_versions_compare_by_release_number()
    {
        InstallManifest("a.plugin", "0.1.0-alpha");

        var row = PluginCatalog.Build(Index(Entry("a.plugin", "0.1.0-beta")), _pluginsRoot).Single();

        row.State.Should().Be(PluginInstallState.UpToDate, "same 0.1.0 release core — not treated as an update");
    }

    [Fact]
    public void A_revoked_build_is_flagged_and_cannot_be_installed()
    {
        var index = new PluginIndex(1, [Entry("bad.plugin", "1.0.0")],
            Revoked: [new PluginFeedRevocation(Id: "bad.plugin", Reason: "malware")]);

        var row = PluginCatalog.Build(index, _pluginsRoot).Single();

        row.Revoked.Should().BeTrue();
        row.RevokedReason.Should().Be("malware");
        row.CanInstall.Should().BeFalse("a revoked build must never be installable from the catalog");
    }

    [Fact]
    public void A_null_index_is_an_empty_catalog()
        => PluginCatalog.Build(null, _pluginsRoot).Should().BeEmpty();

    // ── search ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Search_matches_name_publisher_and_tags_case_insensitively()
    {
        var index = new PluginIndex(1,
        [
            Entry("of.imbalance", "1.0.0") with { Name = "Order-Flow Imbalance", Publisher = "acme", Tags = ["orderflow"] },
            Entry("mean.rev", "1.0.0") with { Name = "Mean Reversion", Publisher = "beta", Tags = ["stat-arb"] },
        ]);
        var catalog = PluginCatalog.Build(index, _pluginsRoot);

        PluginCatalog.Search(catalog, "ORDER").Should().ContainSingle().Which.Id.Should().Be("of.imbalance");
        PluginCatalog.Search(catalog, "beta").Should().ContainSingle().Which.Id.Should().Be("mean.rev");
        PluginCatalog.Search(catalog, "stat-arb").Should().ContainSingle().Which.Id.Should().Be("mean.rev");
        PluginCatalog.Search(catalog, "  ").Should().HaveCount(2, "a blank query returns everything");
    }

    // ── revocation sync ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_writes_feed_revocations_into_the_local_kill_list()
    {
        var index = new PluginIndex(1, [Entry("a.plugin", "1.0.0")],
            Revoked:
            [
                new PluginFeedRevocation(Sha256: "ABCD", Reason: "bad build"),
                new PluginFeedRevocation(Id: "evil.plugin", Reason: "withdrawn"),
            ]);

        PluginRevocationSync.Apply(_pluginsRoot, index).Should().Be(2);

        var list = PluginRevocationList.Load(_pluginsRoot);
        list.IsRevoked("ABCD", pluginId: null, out var r1).Should().BeTrue();
        r1.Should().Be("bad build");
        list.IsRevoked("something-else", "evil.plugin", out _).Should().BeTrue();
    }

    [Fact]
    public void Apply_preserves_existing_local_revocations_and_dedupes()
    {
        PluginRevocationList.Merge(_pluginsRoot, [new RevokedPlugin(Id: "local.only", Reason: "hand-added")]);

        var index = new PluginIndex(1, [],
            Revoked: [new PluginFeedRevocation(Id: "local.only", Reason: "feed-confirmed"),
                      new PluginFeedRevocation(Id: "feed.new", Reason: "new")]);

        PluginRevocationSync.Apply(_pluginsRoot, index).Should().Be(2, "the duplicate id is merged, not doubled");

        var list = PluginRevocationList.Load(_pluginsRoot);
        list.IsRevoked("x", "local.only", out var reason).Should().BeTrue();
        reason.Should().Be("feed-confirmed", "the feed's reason text wins on a merge");
        list.IsRevoked("x", "feed.new", out _).Should().BeTrue();
    }

    [Fact]
    public void Apply_with_no_feed_revocations_is_a_noop()
        => PluginRevocationSync.Apply(_pluginsRoot, new PluginIndex(1, [])).Should().Be(0);

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static PluginFeedVersion Version(string v) =>
        new(v, SdkInfo.Version, $"https://feed.example/{v}.daxplugin", "AA");

    private static PluginFeedEntry Entry(string id, string version) =>
        new(id, id, "Publisher", "A test plugin.", Version(version));

    private static PluginIndex Index(params PluginFeedEntry[] entries) => new(1, entries);

    private void InstallManifest(string id, string version)
    {
        var dir = Path.Combine(_pluginsRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifest.FileName),
            $$"""{ "id":"{{id}}","name":"{{id}}","version":"{{version}}","targetSdkVersion":"{{SdkInfo.Version}}" }""");
    }
}
