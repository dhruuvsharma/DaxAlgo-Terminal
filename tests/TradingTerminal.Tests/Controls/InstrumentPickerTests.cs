using System.Windows;
using FluentAssertions;
using TradingTerminal.UI;
using TradingTerminal.UI.Controls;
using Xunit;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// Regression test for the strategy-window crash "Cannot find resource named
/// 'InstrumentTagsConverter'": the dropdown row template references the converter via a
/// <c>Binding.Converter</c> StaticResource, which only resolves once the converter is registered in
/// <see cref="Application"/> resources. Realizing the control's template used to throw a
/// XamlParseException; it must not anymore.
/// </summary>
public sealed class InstrumentPickerTests
{
    // Runs on WpfTestApp's thread, not xUnit's: the Application it needs belongs to one thread for the
    // whole run (see WpfTestApp).
    [Fact]
    public void Realizing_the_row_template_resolves_the_tags_converter()
    {
        WpfTestApp.Run(() =>
        {
            var picker = new InstrumentPicker
            {
                ItemsSource = SignalInstrumentCatalog.All,
                SelectedInstrument = SignalInstrumentCatalog.All[0], // realizes the selection-box ItemTemplate
            };

            // Force template application + layout. With the bug present this threw a XamlParseException
            // ("Cannot find resource named 'InstrumentTagsConverter'") exactly as in the strategy window.
            var host = new Window
            {
                Content = picker,
                Width = 400,
                Height = 120,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            // Show() is what realizes the ComboBox selection-box ItemTemplate (and the optimized template
            // content) — the exact path in the reported stack (Window.Show → measure → LoadTemplateContent).
            var act = () =>
            {
                try { host.Show(); }
                finally { host.Close(); }
            };

            act.Should().NotThrow();

            // And the ctor seeded the app-level fallback the template's StaticResource resolves to.
            WpfTestApp.Current.Resources.Contains(InstrumentPicker.TagsConverterKey).Should().BeTrue();
        });
    }
}
