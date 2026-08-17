using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Dev-only catalog fixtures for the launch profiles.
///
/// <para>The terminal ships with an <b>empty</b> strategy catalog — the first-party strategies moved
/// to their own repository and no strategy DLL is loaded any more. That is correct for a release, but
/// it makes two things awkward to work on: the first-run experience (which should not look broken)
/// and the catalog UI itself (cards, pills, badges, selection), which needs something to render.</para>
///
/// <para>These fixtures fill that gap. They are <b>not</b> strategies: no signals, no engine
/// counterpart, no market-data subscription. Each one says so on its own card, so a seeded catalog can
/// never be mistaken for a working one. They are gated behind <see cref="DevOptions"/> and every
/// shipped <c>appsettings.json</c> leaves them off.</para>
/// </summary>
public static class DevCatalogSeed
{
    /// <summary>Id of the fixed test-fixture strategy seeded by <see cref="DevOptions.SeedCatalogFixtures"/>.</summary>
    public const string FixtureStrategyId = "fixture.test-strategy";

    /// <summary>Id of the fixed test-fixture visualizer seeded by <see cref="DevOptions.SeedCatalogFixtures"/>.</summary>
    public const string FixtureVisualizerId = "fixture.test-visualizer";

    /// <summary>
    /// The visualizer card for the Testing profile. A descriptor only — visualizer cards are added
    /// straight to the catalog by the shell rather than going through <see cref="IStrategyFactory"/>.
    /// </summary>
    public static readonly VisualizerDescriptor FixtureVisualizer = new(
        FixtureVisualizerId,
        "Test Visualizer (fixture)",
        "Placeholder visualizer card for UI and integration testing. Renders nothing.",
        DataRequirementTags: ["FIXTURE"]);

    /// <summary>
    /// The cards <paramref name="dev"/> asks for, as (strategy, registration) pairs ready to hand to
    /// DI. The shell registers them — this project is a published SDK contract package, so it stays
    /// free of a dependency-injection dependency for the sake of dev-only scaffolding.
    ///
    /// <para>Registering them is all that is needed: <see cref="IStrategyFactory"/> takes
    /// <c>IEnumerable&lt;ITradingStrategy&gt;</c>, so seeded cards arrive through the ordinary seam
    /// and nothing else in the shell has to know they exist.</para>
    /// </summary>
    public static IReadOnlyList<(ITradingStrategy Strategy, StrategyFactoryRegistration Registration)>
        Build(DevOptions dev)
    {
        ArgumentNullException.ThrowIfNull(dev);
        var seeded = new List<(ITradingStrategy, StrategyFactoryRegistration)>();

        if (dev.SeedCatalogFixtures)
            seeded.Add(Create(FixtureStrategyId, "Test Strategy (fixture)",
                "Placeholder strategy card for UI and integration testing. Generates no signals."));

        for (var i = 1; i <= dev.SeedSampleStrategies; i++)
            seeded.Add(Create($"sample.strategy-{i}", $"Sample Strategy {i}",
                "Sample catalog entry for first-run and layout work. Generates no signals."));

        return seeded;
    }

    private static (ITradingStrategy, StrategyFactoryRegistration) Create(
        string id, string name, string description) =>
        (new SeededStrategy(id, name, description),
         new StrategyFactoryRegistration(
             id,
             _ => BuildPlaceholderView(name, description),
             _ => new object()));

    /// <summary>
    /// What opening a seeded card shows. A card that silently does nothing when clicked reads as a
    /// bug, so the fixture is explicit about being a fixture instead.
    /// </summary>
    private static object BuildPlaceholderView(string name, string description) => new Border
    {
        Padding = new Thickness(28),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = name,
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = description,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 0, 0, 18),
                    TextWrapping = TextWrapping.Wrap,
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = "Seeded by a development launch profile. This is not a real strategy — "
                             + "it holds a place in the catalog so the cards and the shell can be "
                             + "exercised without any compiled artifact.",
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        },
    };

    /// <summary>Metadata-only <see cref="ITradingStrategy"/>. Every behavioural member keeps its
    /// interface default, so this stays a card and nothing more.</summary>
    private sealed class SeededStrategy(string id, string displayName, string description) : ITradingStrategy
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public string Description { get; } = description;
    }
}
