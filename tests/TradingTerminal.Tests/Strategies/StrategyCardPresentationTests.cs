using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FluentAssertions;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Tests.Controls;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class StrategyCardPresentationTests
{
    [Fact]
    public void Research_paper_is_the_default_generic_link()
    {
        var item = new StrategyCatalogItemViewModel(new PaperStrategy(), StrategyPresentation.Empty);

        item.LinkUrl.Should().Be("https://example.test/paper");
        item.LinkUri.Should().Be(new Uri("https://example.test/paper"));
        item.HasLink.Should().BeTrue();
    }

    [Fact]
    public void Editor_builds_a_trimmed_link_override_and_reset_restores_the_strategy_default()
    {
        var item = new StrategyCatalogItemViewModel(new PaperStrategy(), StrategyPresentation.Empty);
        var editor = new StrategyPresentationEditorViewModel(item)
        {
            LinkUrl = "  https://social.example/author  ",
        };

        editor.Build().LinkUrl.Should().Be("https://social.example/author");

        editor.ResetToDefaultCommand.Execute(null);

        editor.LinkUrl.Should().Be("https://example.test/paper");
        editor.Build().LinkUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    public void Non_https_links_cannot_be_opened(string link)
    {
        var item = new StrategyCatalogItemViewModel(
            new PlainStrategy(),
            new StrategyPresentation(LinkUrl: link));

        item.HasLink.Should().BeFalse();
        item.LinkUri.Should().BeNull();
    }

    [Fact]
    public void Card_actions_use_a_vertical_accessible_icon_rail_and_realize_the_tag_flyout()
    {
        WpfTestApp.Run(() =>
        {
            var item = new StrategyCatalogItemViewModel(
                new PaperStrategy(),
                new StrategyPresentation(Tags: ["mean-reversion", "intraday"]));
            var actions = new StrategyCardActions { DataContext = item };
            var host = new Window
            {
                Content = actions,
                Width = 400,
                Height = 160,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            var act = () =>
            {
                try
                {
                    host.Show();
                    host.UpdateLayout();

                    var rail = (StackPanel)actions.FindName("ActionRail");
                    var tagsToggle = (ToggleButton)actions.FindName("TagsToggle");
                    var linkButton = (Button)actions.FindName("LinkButton");
                    var popup = (Popup)actions.FindName("TagsPopup");

                    rail.Orientation.Should().Be(Orientation.Vertical);
                    tagsToggle.Content.Should().BeOfType<Viewbox>();
                    linkButton.Content.Should().BeOfType<Viewbox>();
                    AutomationProperties.GetName(tagsToggle).Should().Be("Show strategy tags");
                    AutomationProperties.GetName(linkButton).Should().Be("Open strategy link");
                    AutomationProperties.GetHelpText(linkButton).Should().Contain("https://example.test/paper");

                    tagsToggle.IsChecked = true;
                    host.UpdateLayout();

                    popup.IsOpen.Should().BeTrue();
                    popup.Placement.Should().Be(PlacementMode.Left);
                    ((ItemsControl)actions.FindName("DataTagList")).Items.Count.Should().Be(2);
                    ((ItemsControl)actions.FindName("ClassificationTagList")).Items.Count.Should().Be(3);
                    ((ItemsControl)actions.FindName("CustomTagList")).Items.Count.Should().Be(2);
                }
                finally
                {
                    host.Close();
                }
            };

            act.Should().NotThrow();
        });
    }

    private sealed class PaperStrategy : ITradingStrategy
    {
        public string Id => "test.paper";
        public string DisplayName => "Paper strategy";
        public string Description => "Derived from published research.";
        public string? ResearchPaperUrl => "https://example.test/paper";
    }

    private sealed class PlainStrategy : ITradingStrategy
    {
        public string Id => "test.plain";
        public string DisplayName => "Plain strategy";
        public string Description => "No compiled link.";
    }
}
