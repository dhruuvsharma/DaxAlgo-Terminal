using FluentAssertions;
using TradingTerminal.UI.Theming;
using Xunit;

namespace TradingTerminal.Tests.Controls;

public sealed class ThemeManagerTests
{
    [Fact]
    public void Built_in_registry_exposes_only_daxalgo_dark_and_light()
    {
        var manager = new ThemeManager();

        manager.Themes.Select(theme => $"{theme.Id}|{theme.Name}").Should().Equal(
            "daxalgo-dark|DaxAlgo Dark",
            "daxalgo-light|DaxAlgo Light");
    }
}
