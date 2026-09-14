using System.IO;
using System.Windows;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Blocks.Runtime.Tests;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.Blocks.WebHost.Tests;

/// <summary>
/// A registered Blocks unit opened the way the catalog opens it: its page inside the terminal's unit
/// window, the settings panel built from what the unit declares, the market feed started for what it
/// subscribes to, the book row fed from its portfolio — and all of it released on close.
/// </summary>
public sealed class TheUnitWindowHostsThePageTests
{
    [Fact]
    public async Task A_registered_strategy_opens_with_its_page_its_settings_its_feed_and_its_book()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var hub = new FakeHub();
        var feeds = new List<string>();
        var host = Host.For(hub) with
        {
            OpenFeed = (instrument, feed, _) =>
            {
                lock (feeds) feeds.Add($"{instrument.Value}:{feed}");
                return new Released(() => { lock (feeds) feeds.Remove($"{instrument.Value}:{feed}"); });
            },
        };

        var pinged = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new BlocksUnitRegistration(
            "ticker", "Ticker", "Follows the mid.", IsStrategy: true,
            () => new Ticker(pinged),
            [new StrategyFile("ui/index.html", Page)],
            []);

        using var sta = await StaHost.StartAsync("unit window test");

        var (session, window) = await sta.InvokeAsync<(BlocksUnitSession, Window)>(() =>
        {
            // Off-screen, so the browser is told not to treat the window as hidden — the probe's arguments.
            var root = Path.Combine(Path.GetTempPath(), "daxalgo-unit-window-tests");
            var session = BlocksUnitSession.Create(
                registration, host,
                viewOptions: new WebUnitViewOptions(Path.Combine(root, "webview2"), "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows"),
                pageRoot: Path.Combine(root, "pages"));
            var window = new Window
            {
                Content = new AuthoredUnitView { DataContext = session.Unit.Presenter },
                Width = 900, Height = 600, Left = -32000, Top = -32000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            return Task.FromResult((session, window));
        });

        try
        {
            try
            {
                await sta.InvokeAsync(() => session.StartAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                var state = await sta.InvokeAsync(() => Task.FromResult(
                    $"loaded={session.Page?.IsLoaded} visible={session.Page?.IsVisible} size={session.Page?.ActualWidth}x{session.Page?.ActualHeight} "
                    + $"hasPage={session.Unit.Presenter.HasPageContent} viewLoaded={((FrameworkElement)window.Content).IsLoaded} runtime={session.Runtime.State} "
                    + Describe((DependencyObject)window.Content, 0)));
                throw new TimeoutException(state);
            }

            session.Unit.Presenter.PageContent.Should().BeSameAs(session.Page);
            session.Unit.Presenter.HasBook.Should().BeTrue();
            session.Unit.Presenter.Parameters.Select(p => p.Key).Should().Equal("instrument");
            feeds.Should().Contain("1:Quotes", "the unit's subscription started the venue stream behind it");

            hub.PublishQuote(FakeHub.Quote(1, 99.5, 100.5));

            var mid = await pinged.Task.WaitAsync(TimeSpan.FromSeconds(20));
            mid.Should().Be(100, "the page received the unit's state and answered it");

            await WaitUntil(() => session.Unit.Presenter.Book.PositionUnits == 1, "the book row follows the unit's orders");
        }
        finally
        {
            await sta.InvokeAsync(async () =>
            {
                await session.DisposeAsync();
                window.Close();
            }).WaitAsync(TimeSpan.FromSeconds(30));
        }

        feeds.Should().BeEmpty("closing the window releases every stream the unit started");
    }

    private const string Page = """
        <body style="background:#0e1117;color:#e6edf3;font:32px Segoe UI"><div id="mid">waiting</div>
        <script>
          dax.on('state', s => { mid.textContent = s.mid; dax.send('seen', { mid: s.mid }); });
          dax.ready();
        </script></body>
        """;

    private static string Describe(DependencyObject node, int depth)
    {
        if (depth > 4) return string.Empty;
        var text = node is FrameworkElement fe ? $"[{node.GetType().Name} vis={fe.Visibility} {fe.ActualWidth:0}x{fe.ActualHeight:0}]" : string.Empty;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            text += Describe(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        return text;
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(20);
        }
    }

    private sealed class Released(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>Buys one on the first quote, tells its page the mid, and reports what the page answers.</summary>
    private sealed class Ticker(TaskCompletionSource<double> pinged) : IUnit
    {
        public UnitInfo Info { get; } = new("Ticker", Settings: [StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1))]);

        public Task StartAsync(IUnitContext context, CancellationToken ct)
        {
            var instrument = context.Settings.Instrument("instrument");
            context.Market.OnQuote(instrument, q =>
            {
                context.Orders.SetTarget(instrument, 1);
                context.Ui.Send("state", new { mid = q.Mid });
            });
            context.Ui.On("seen", payload => pinged.TrySetResult(payload.GetProperty("mid").GetDouble()));
            return Task.CompletedTask;
        }
    }
}
