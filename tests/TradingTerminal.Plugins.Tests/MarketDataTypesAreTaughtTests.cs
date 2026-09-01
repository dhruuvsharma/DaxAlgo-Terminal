using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The surface must define the types a unit's callbacks actually receive.
///
/// <para><b>Found by a live run that would not compile.</b> A volume-footprint brief reached for
/// <c>FeedQuality.Partial</c> — a member that does not exist, on a type it was never shown. Chasing it
/// found the systematic version: <c>SdkSurfaceGenerator</c> reflects
/// <c>typeof(IStrategyKernel).Assembly</c>, which is <c>DaxAlgo.Sdk</c> alone, and every market-data
/// type lives in <c>TradingTerminal.Core</c>. So the surface prints
/// <c>OnBarAsync(OhlcvBar bar, …)</c> and never says what an <c>OhlcvBar</c> holds.</para>
///
/// <para>It went unnoticed because <c>bar.Close</c>, <c>quote.Bid</c> and <c>depth.Bids[i].Price</c>
/// are guessable. <c>FootprintBar</c> is not — and <c>Footprint.Draw</c> takes a list of them, so the
/// drawing library asks for a type the surface does not define.</para>
/// </summary>
public sealed class MarketDataTypesAreTaughtTests
{
    /// <summary>Every type a unit is handed by the contracts, or handed to a widget.</summary>
    public static TheoryData<string> Handed =>
    [
        "OhlcvBar",        // OnBarAsync
        "Quote",           // OnQuoteAsync
        "TradePrint",      // OnTradeAsync
        "DepthSnapshot",   // OnDepthAsync
        "DepthLevel",      // inside a DepthSnapshot
        "FootprintBar",    // Footprint.Draw
        "InstrumentId",    // every callback and every parameter
    ];

    [Theory]
    [MemberData(nameof(Handed))]
    public void A_type_a_callback_receives_is_defined_in_the_surface(string type)
    {
        // The type's own block, not a mention inside a signature. Being named as a parameter is what
        // the surface already did, and is exactly what left the model guessing.
        SdkSurfaceGenerator.Generate()
            .Should().Contain(
                $"### `{type}`",
                $"a unit is handed a {type} and must know what is on it");
    }

    /// <summary>The types a unit has to BUILD rather than merely read, with the parameter that names
    /// each one's constructor.</summary>
    public static TheoryData<string, string> Built { get; } = new()
    {
        // Footprint.Draw takes a list of these, and the host hands a unit raw TradePrints — so a
        // footprint visualizer has no route to a picture that does not go through building them.
        { "FootprintBar", "IReadOnlyList<FootprintFeatureRow>" },
        { "FootprintFeatureRow", "double Price" },
        // An SDK widget, and it failed the same way: "'Tile' does not contain a constructor that takes
        // 5 arguments". So this was never only about the Core types.
        { "Tile", "string" },
    };

    [Theory]
    [MemberData(nameof(Built))]
    public void A_type_a_unit_must_construct_shows_its_constructor(string type, string parameter)
    {
        // THE SAME DEFECT ONE LEVEL DEEPER, and the retest is what exposed it. Teaching FeedQuality
        // fixed the hallucinated enum member — three generations later the model used the real one —
        // and then failed on "'FootprintBar' does not contain a constructor that takes 15 arguments",
        // followed by an object initializer against get-only properties.
        //
        // The surface prints every property of a positional record and never its primary constructor,
        // which for a record IS its shape. IsInteresting keeps methods and properties; Mentions has a
        // ConstructorInfo arm that nothing could ever reach.
        var block = Block(SdkSurfaceGenerator.Generate(), type);

        block.Should().Contain($"{type}(", $"a unit must be able to construct a {type}");
        block.Should().Contain(parameter);
    }

    /// <summary>One type's section of the surface, so a match cannot come from a neighbour.</summary>
    private static string Block(string surface, string type)
    {
        var start = surface.IndexOf($"### `{type}`", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{type} must be defined in the surface at all");

        var end = surface.IndexOf(SdkSurfaceGenerator.MarkerPrefix, start, StringComparison.Ordinal);
        return end < 0 ? surface[start..] : surface[start..end];
    }

    [Fact]
    public void The_types_it_teaches_are_derived_from_the_signatures_it_prints()
    {
        // Derived rather than listed, so it cannot drift — the same property the generator exists for.
        // A type that stops appearing in a signature stops being taught, and a new one starts, without
        // anybody maintaining a roster.
        var surface = SdkSurfaceGenerator.Generate();

        // Nothing outside the contracts leaks in: the host's own plumbing is not a unit's vocabulary.
        surface.Should().NotContain("### `MarketDataRepository`");
        surface.Should().NotContain("### `IBrokerClient`");
    }
}
