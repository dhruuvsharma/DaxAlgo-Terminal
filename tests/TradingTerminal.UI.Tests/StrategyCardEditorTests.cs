using System;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// "Edit card" on an authored or hosted card.
///
/// <para><b>The bug this was written for.</b> The editor worked out a card's defaults for itself as
/// <c>item.Strategy?.DisplayName ?? item.Visualizer!.DisplayName</c>, a rule that was correct when a
/// card had two backings. An authored kernel and a Blocks unit have neither, so clicking "Edit
/// strategy card…" on either one threw a NullReferenceException out of the constructor and took the
/// app down. The catalog item had already been fixed for the same null, but the editor kept its own
/// copy of the rule. Both now read the item's <c>DefaultName</c> and <c>DefaultDescription</c>.</para>
/// </summary>
public sealed class StrategyCardEditorTests
{
    private static StrategyKernelRegistration Kernel() =>
        new(new VisualizerDescriptor("authored.flow", "Authored flow", "An authored kernel"),
            () => throw new NotSupportedException("the editor never runs the unit"));

    private static HostedCatalogUnit Hosted(bool isStrategy) =>
        new("blocks.pressure", "Blocks pressure", "A Blocks unit", isStrategy);

    public static TheoryData<string, StrategyCatalogItemViewModel> CardsWithoutAPluginStrategy => new()
    {
        { "authored kernel", new StrategyCatalogItemViewModel(Kernel(), StrategyPresentation.Empty) },
        { "hosted strategy", new StrategyCatalogItemViewModel(Hosted(isStrategy: true), StrategyPresentation.Empty) },
        { "hosted visualizer", new StrategyCatalogItemViewModel(Hosted(isStrategy: false), StrategyPresentation.Empty) },
        {
            "visualizer",
            new StrategyCatalogItemViewModel(
                new VisualizerDescriptor("viz.depth", "Depth", "A visualizer"), StrategyPresentation.Empty)
        },
    };

    [Theory]
    [MemberData(nameof(CardsWithoutAPluginStrategy))]
    public void The_editor_opens_on_every_backing(string backing, StrategyCatalogItemViewModel card)
    {
        var editor = new StrategyPresentationEditorViewModel(card);

        Assert.True(card.DefaultName.Length > 0, backing);
        Assert.Equal(card.DefaultName, editor.DefaultName);
        Assert.Equal(card.DefaultDescription, editor.DefaultDescription);
        Assert.Equal(card.Name, editor.Name);
    }

    [Theory]
    [MemberData(nameof(CardsWithoutAPluginStrategy))]
    public void Saving_an_untouched_card_stores_no_override(string backing, StrategyCatalogItemViewModel card)
    {
        var saved = new StrategyPresentationEditorViewModel(card).Build();

        Assert.True(saved.Name is null, backing);
        Assert.True(saved.Description is null, backing);
        Assert.True(saved.LinkUrl is null, backing);
    }

    [Fact]
    public void Reset_restores_the_units_own_name()
    {
        var editor = new StrategyPresentationEditorViewModel(
            new StrategyCatalogItemViewModel(Kernel(), new StrategyPresentation(Name: "Renamed")));

        Assert.Equal("Renamed", editor.Name);

        editor.ResetToDefaultCommand.Execute(null);

        Assert.Equal("Authored flow", editor.Name);
        Assert.Null(editor.Build().Name);
    }
}
