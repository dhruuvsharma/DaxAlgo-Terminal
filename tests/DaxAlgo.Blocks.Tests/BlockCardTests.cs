using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;
using Xunit.Abstractions;

namespace DaxAlgo.Blocks.Tests;

/// <summary>
/// The cards are the whole of what Hyperion is told about the SDK, so they must describe the blocks
/// that exist, cover every one of them, and stay small.
/// </summary>
public sealed class BlockCardTests(ITestOutputHelper output)
{
    [Fact]
    public void The_committed_cards_match_the_blocks()
    {
        // Rewrites the files before failing: the fix for a red build here is to review the diff and
        // commit it, which is what should happen when a block's contract changes.
        var rewritten = BlockCatalogGenerator.WriteTo(RepositoryRoot());

        rewritten.Should().BeFalse(
            $"the block cards were stale and have just been rewritten under {BlockCatalogGenerator.RelativeDirectory}. "
            + "Review the diff and commit it.");
    }

    [Fact]
    public void Every_block_on_the_context_has_a_card()
    {
        // A block a unit can reach but the agent is never told about is a block that never gets used —
        // or gets guessed at.
        var carded = typeof(IUnit).Assembly.GetExportedTypes()
            .Where(type => type.GetCustomAttribute<BlockCardAttribute>() is not null)
            .ToHashSet();

        foreach (var property in typeof(IUnitContext).GetProperties())
            carded.Should().Contain(property.PropertyType, "{0} is a block on the context", property.Name);

        carded.Should().Contain(typeof(IUnit));
    }

    [Fact]
    public void Card_ids_are_unique()
    {
        BlockCatalogGenerator.Cards().Select(card => card.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_maths_type_is_on_exactly_one_maths_card()
    {
        // The maths library is split into slices so a task needing indicators is not also sent the
        // learners. A type on no slice is invisible to the agent; a type on two is paid for twice.
        var quant = typeof(DaxAlgo.Sdk.Quant.Num).Assembly.GetExportedTypes().Where(type => !type.IsNested);

        var slices = typeof(IUnit).Assembly.GetCustomAttributes<BlockCardAttribute>()
            .Where(card => card.Id.StartsWith("math.", StringComparison.Ordinal))
            .ToArray();

        foreach (var type in quant)
            slices.Count(slice => slice.Types.Contains(type)).Should().Be(1, "{0} must be on exactly one maths card", type.Name);
    }

    [Fact]
    public void Cards_stay_small_and_their_sizes_are_reported()
    {
        // The point of cards is that a call carries a few of them instead of the whole SDK. These are
        // ceilings with headroom, not targets: they exist so a card that quietly doubles fails.
        var cards = BlockCatalogGenerator.Cards();
        var index = BlockCatalogGenerator.Index(cards);
        var conventions = File.ReadAllText(Path.Combine(
            RepositoryRoot(), BlockCatalogGenerator.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar), "conventions.md"));

        foreach (var card in cards)
            output.WriteLine($"{card.Id,-18} {card.Markdown.Length,6:N0} chars  ~{card.Markdown.Length / 4,5:N0} tokens");
        output.WriteLine($"{"index",-18} {index.Length,6:N0} chars  ~{index.Length / 4,5:N0} tokens");
        output.WriteLine($"{"conventions",-18} {conventions.Length,6:N0} chars  ~{conventions.Length / 4,5:N0} tokens");
        output.WriteLine($"{"ALL CARDS",-18} {cards.Sum(c => c.Markdown.Length),6:N0} chars  ~{cards.Sum(c => c.Markdown.Length) / 4,5:N0} tokens");

        foreach (var card in cards)
            card.Markdown.Length.Should().BeLessThan(6_000, "the {0} card should be a card, not a manual", card.Id);

        index.Length.Should().BeLessThan(2_500);
        conventions.Length.Should().BeLessThan(8_000);
    }

    [Fact]
    public void A_card_shows_what_a_block_does_and_never_how()
    {
        foreach (var card in BlockCatalogGenerator.Cards())
        {
            card.Markdown.Should().NotContain("private ", "{0} leaks implementation", card.Id);
            card.Markdown.Should().NotContain("internal ", "{0} leaks implementation", card.Id);
            card.Markdown.Should().NotContain("\f", "{0} carries a paragraph of rationale", card.Id);
        }
    }

    [Fact]
    public void The_blocks_sdk_cannot_reach_the_widget_library()
    {
        // The whole reason it does not reference DaxAlgo.Sdk: a Blocks unit ships its own page, and the
        // drawing surface and widgets must not be reachable from it.
        typeof(IUnit).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should().NotContain("DaxAlgo.Sdk")
            .And.NotContain("DaxAlgo.Sdk.Wpf")
            .And.NotContain("TradingTerminal.UI");
    }

    /// <summary>The repository root, whichever of the three checkouts this runs in.</summary>
    internal static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        // <root>/tests/DaxAlgo.Blocks.Tests/BlockCardTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        Directory.EnumerateFiles(root, "*.slnx").Should().NotBeEmpty($"'{root}' should be a repository root");
        return root;
    }
}
