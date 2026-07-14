using System.Windows;
using FluentAssertions;
using TradingTerminal.Charts;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using Xunit;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// The three chart tools are now embeddable <c>UserControl</c>s (an authored strategy composes its live
/// window out of them), with the standalone windows reduced to frames around them. Two things can only
/// break at runtime, so they are pinned here:
/// <list type="number">
/// <item>the panel XAML still <b>parses</b> — every <c>StaticResource</c> it names resolves against the
/// real theme dictionaries, and the <c>Features</c> gates bind through <c>RelativeSource AncestorType</c>
/// to the control itself (a typo there throws only when the tree is realized, never at compile time);</item>
/// <item>a gated-off feature is really off — the presets are what strategy windows will be handed, so a
/// preset that quietly left the ML forecaster on would train a model nobody asked for.</item>
/// </list>
/// <para>The XAML tests run on <see cref="WpfTestApp"/>'s thread, not xUnit's — see the note there.</para>
/// </summary>
public sealed class ChartPanelTests
{
    [Fact]
    public void Every_panel_parses_and_defaults_to_all_features_on()
    {
        WpfTestApp.Run(() =>
        {
            var act = () =>
            {
                _ = new OrderBookPanel();
                _ = new VolumeFootprintPanel();
                _ = new ChartsPanel();
            };

            act.Should().NotThrow();

            // A panel dropped into a window with no Features set must behave like the tool it came from.
            new OrderBookPanel().Features.Should().BeSameAs(OrderBookPanelFeatures.Full);
            new VolumeFootprintPanel().Features.Should().BeSameAs(VolumeFootprintPanelFeatures.Full);
            new ChartsPanel().Features.Should().BeSameAs(ChartsPanelFeatures.Full);
        });
    }

    [Fact]
    public void A_gated_panel_can_be_realized_with_its_features_off()
    {
        WpfTestApp.Run(() =>
        {
            // Realizing the tree is what actually evaluates the Features bindings and the StaticResources
            // inside the collapsed sections — constructing the control alone would not.
            var host = new Window
            {
                Content = new OrderBookPanel { Features = OrderBookPanelFeatures.LadderOnly },
                Width = 600,
                Height = 400,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            var act = () =>
            {
                try { host.Show(); }
                finally { host.Close(); }
            };

            act.Should().NotThrow();
        });
    }

    [Fact]
    public void The_embedded_presets_never_train_a_model()
    {
        // What an authored strategy gets by default. ML is the one feature that costs real work even
        // when nothing is looking at it (warm-start replay + per-tick training), so no preset short of
        // Full may leave it on.
        OrderBookPanelFeatures.Embedded.MlForecast.Should().BeFalse();
        OrderBookPanelFeatures.LadderOnly.MlForecast.Should().BeFalse();
        VolumeFootprintPanelFeatures.Embedded.MlForecast.Should().BeFalse();
        VolumeFootprintPanelFeatures.ChartOnly.MlForecast.Should().BeFalse();

        // …and the regression fits, which are the footprint's other per-bar cost.
        VolumeFootprintPanelFeatures.Embedded.Regression.Should().BeFalse();

        // The standalone windows still get everything.
        OrderBookPanelFeatures.Full.MlForecast.Should().BeTrue();
        VolumeFootprintPanelFeatures.Full.MlForecast.Should().BeTrue();
        VolumeFootprintPanelFeatures.Full.Regression.Should().BeTrue();
        ChartsPanelFeatures.Full.Indicators.Should().BeTrue();
    }

    [Fact]
    public void Chrome_is_off_in_every_embedded_preset()
    {
        // A strategy window owns the instrument. A panel toolbar would let the user point one panel at a
        // different symbol than the strategy is trading, so embedding always drops it.
        OrderBookPanelFeatures.Embedded.Toolbar.Should().BeFalse();
        VolumeFootprintPanelFeatures.Embedded.Toolbar.Should().BeFalse();
        ChartsPanelFeatures.Embedded.Toolbar.Should().BeFalse();
    }
}
