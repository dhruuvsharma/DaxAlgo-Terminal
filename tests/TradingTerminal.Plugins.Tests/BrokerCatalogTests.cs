using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The broker catalogue.
///
/// <para>The failure mode for a list like this is drift: an entry claiming an adapter that was never
/// written, a logo file that does not exist, a slug that stops matching its asset. None of it breaks a
/// build, all of it reaches a user as a picker offering something the terminal cannot do.</para>
/// </summary>
public sealed class BrokerCatalogTests
{
    /// <summary>The repository root, from this file's own compile-time path — build output is redirected
    /// outside the source tree, so walking up from the binary never finds it.</summary>
    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // <root>/tests/TradingTerminal.Plugins.Tests/BrokerCatalogTests.cs
        var directory = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }

    [Fact]
    public void EverySlugIsUnique()
    {
        // The slug is also the logo file name and the lookup key. A duplicate silently shadows.
        BrokerCatalog.All.Select(b => b.Id)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryDisplayNameIsUnique()
    {
        // Two rows reading the same in a picker is a coin flip for the user.
        BrokerCatalog.All.Select(b => b.DisplayName)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EverySlugIsAUsableFileName()
    {
        BrokerCatalog.All.Select(b => b.Id)
            .Should().OnlyContain(id => id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.'));
    }

    [Fact]
    public void EveryEntryNamesItsOwner()
    {
        // The domain is the provenance for a third-party mark, and it is what a "learn more" link needs.
        BrokerCatalog.All.Should().OnlyContain(b => b.Domain.Contains('.'));
    }

    [Fact]
    public void EveryEntryTradesSomething()
    {
        BrokerCatalog.All.Should().OnlyContain(b => b.Assets != BrokerAssets.None);
    }

    // ── the claims that could be false ──────────────────────────────────────────────────────────

    [Fact]
    public void AnEntryClaimingAnAdapterHasOne()
    {
        // Anything past Planned says a user can connect it. If the wire-level source is missing, that
        // claim has nothing behind it.
        BrokerCatalog.All
            .Where(b => b.Status != BrokerStatus.Planned)
            .Should().OnlyContain(b => b.Kind != null, "a connectable broker needs a BrokerKind");
    }

    [Fact]
    public void APlannedEntryClaimsNoAdapter()
    {
        // The other direction: a BrokerKind on a Planned row would make it connectable by accident.
        BrokerCatalog.All
            .Where(b => b.Status == BrokerStatus.Planned)
            .Should().OnlyContain(b => b.Kind == null);
    }

    [Fact]
    public void NoTwoEntriesClaimTheSameAdapter()
    {
        BrokerCatalog.All.Where(b => b.Kind != null).Select(b => b.Kind)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryWiredSourceIsInTheCatalogue()
    {
        // The catalogue is meant to be the answer to "what do we support?". A BrokerKind it has never
        // heard of is a broker the user connects to and the catalogue cannot describe.
        var missing = Enum.GetValues<BrokerKind>()
            .Where(kind => kind != BrokerKind.Simulated)
            .Where(kind => BrokerCatalog.For(kind) is null)
            .ToArray();

        missing.Should().BeEmpty();
    }

    [Fact]
    public void OnlyFullyIntegratedBrokersClaimTheyCanExecute()
    {
        // Order routing is the claim that costs money if it is wrong.
        BrokerCatalog.All.Where(b => b.CanExecute)
            .Should().OnlyContain(b => b.Status == BrokerStatus.Full);
    }

    // ── the marks ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryLogoOnDiskBelongsToACataloguedBroker()
    {
        // An orphan mark is a third-party trademark sitting in a public repository with nothing pointing
        // at it and nothing explaining why it is there.
        var folder = Path.Combine(RepositoryRoot(), "assets", "brokers");
        Directory.Exists(folder).Should().BeTrue();

        var orphans = Directory.EnumerateFiles(folder, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => BrokerCatalog.Find(name!) is null)
            .ToArray();

        orphans.Should().BeEmpty();
    }

    [Fact]
    public void EveryMarkIsDistinct()
    {
        // The favicon service answers an unknown domain with a generic globe rather than failing, so a
        // wrong logo arrives looking exactly like a right one. Identical bytes across two brokers is how
        // that shows up, and two of them were caught this way.
        var folder = Path.Combine(RepositoryRoot(), "assets", "brokers");

        var duplicated = Directory.EnumerateFiles(folder, "*.png")
            .GroupBy(file => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(Path.GetFileName))
            .ToArray();

        duplicated.Should().BeEmpty("identical marks mean at least one is the service's fallback globe");
    }

    [Fact]
    public void ABrokerWithNoMarkIsStillListed()
    {
        // The assets README is explicit: keep the text fallback when a mark is unavailable or unapproved.
        // A missing logo must not remove a broker from the catalogue.
        var folder = Path.Combine(RepositoryRoot(), "assets", "brokers");
        var withoutMark = BrokerCatalog.All
            .Where(b => !File.Exists(Path.Combine(folder, b.Id + ".png")))
            .ToArray();

        withoutMark.Should().OnlyContain(b => b.DisplayName.Length > 0);
    }

    // ── the shape of the list ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCatalogueCoversTheRegionsTheProductTargets()
    {
        foreach (var region in Enum.GetValues<BrokerRegion>())
            BrokerCatalog.All.Should().Contain(b => b.Region == region, $"{region} has no brokers");
    }

    [Fact]
    public void ConnectableAndPlannedTogetherAreEverything()
    {
        (BrokerCatalog.Connectable.Count + BrokerCatalog.Planned.Count)
            .Should().Be(BrokerCatalog.All.Count);
    }

    [Fact]
    public void LookupIsCaseInsensitiveBecauseSlugsGetTypedByHand()
    {
        BrokerCatalog.Find("OANDA").Should().NotBeNull();
        BrokerCatalog.Find("oanda").Should().NotBeNull();
    }
}
