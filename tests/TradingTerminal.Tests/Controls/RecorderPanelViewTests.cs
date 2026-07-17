using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using TradingTerminal.Recording;
using Xunit;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// Parse/realize guard for the recorder panel. Every brush, style and converter the panel resolves is
/// only proven at <b>realize</b> time — a missing <c>StaticResource</c> key throws a XamlParseException
/// the moment the user clicks the header button, not at compile time. The panel is dense with locally
/// declared styles (chips, row cards, the record toggle's two faces), so this is the cheapest place to
/// catch a typo'd key.
///
/// <para>Runs on <see cref="WpfTestApp"/>'s thread with the real theme dictionaries merged, so the
/// DynamicResource theme tokens resolve exactly as they do in the shell.</para>
/// </summary>
public sealed class RecorderPanelViewTests
{
    [Fact]
    public void The_panel_parses_and_realizes_against_the_real_theme()
    {
        WpfTestApp.Run(() =>
        {
            var view = new RecorderPanelView();

            var host = new Window
            {
                Content = view,
                Width = 460,
                Height = 560,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            var act = () =>
            {
                host.Show();
                host.Close();
            };

            act.Should().NotThrow("a missing StaticResource key throws when the user opens the panel");
        });
    }

    /// <summary>The L3 chip is a deliberate, permanently-dim placeholder: no broker in this build
    /// exposes market-by-order. If someone ever wires a real L3 feed, this test failing is the reminder
    /// that the chip has to start reflecting it.</summary>
    [Fact]
    public void L3_is_advertised_as_unavailable()
    {
        RecorderEntry.SupportsL3.Should().BeFalse(
            "no IBrokerClient method, store stream, or broker feed produces L3 in this build");
    }
}
