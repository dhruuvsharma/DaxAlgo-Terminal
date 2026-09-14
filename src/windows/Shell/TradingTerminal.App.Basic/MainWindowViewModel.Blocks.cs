using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Shell;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Blocks.WebHost;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.App;

/// <summary>Opening units written against the Blocks SDK.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Opens the Blocks unit registered under <paramref name="id"/>, when there is one.
    ///
    /// <para>Checked before the other backings because a Blocks card is a strategy or a visualizer by
    /// what its code does, so it arrives through either Open verb. Everything below the window — the
    /// runtime, the page, the settings, the book, the log, the feeds — is <see cref="BlocksUnitSession"/>,
    /// shared with the other shell.</para>
    /// </summary>
    /// <returns>True when <paramref name="id"/> named a Blocks unit and its window was opened.</returns>
    private bool TryOpenBlocksUnit(string id)
    {
        if (_services.GetService<IBlocksUnitRegistry>()?.Find(id) is not { } registration) return false;

        var capturedId = registration.Id;
        _host.OpenWithOverlay($"Opening {registration.DisplayName}…", "Starting the unit and loading its page…", () =>
        {
            AuthoredUnitHost? chrome = null;
            var host = BlocksUnitSession.HostFor(
                _services, LogSink, SelectableInstruments,
                offerExport: (label, text) =>
                {
                    chrome?.TakeAway(label, text);
                    return chrome is not null;
                });

            var session = BlocksUnitSession.Create(registration, host, LogSink, SelectableInstruments());
            chrome = session.Unit;

            var window = ToolHostWindow.Create(registration.DisplayName, new AuthoredUnitView { DataContext = session.Unit.Presenter });
            window.Owner = Application.Current.MainWindow;
            TradingTerminal.UI.StrategyWindowPlacementStore.Attach(window, capturedId);
            window.Closed += async (_, _) =>
            {
                _host.Unregister(capturedId);
                await session.DisposeAsync();
            };

            _host.Register(capturedId, window);
            window.Show();

            // After Show: WebView2 needs the window's handle before the page can load.
            _ = session.StartAsync();
        });

        return true;
    }
}
