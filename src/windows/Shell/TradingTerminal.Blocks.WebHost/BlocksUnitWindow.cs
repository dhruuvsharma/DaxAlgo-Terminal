using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DaxAlgo.Blocks;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// The window a Blocks unit runs in: its page, and the runtime feeding it.
///
/// <para><b>No chrome of the terminal's own inside the frame.</b> The page is the window. A unit with no
/// page runs headless and says so, because a strategy that only trades has nothing to draw and should
/// not be made to pretend.</para>
///
/// <para>The runtime starts when the window loads and stops when it closes; the page is torn down after
/// the unit has stopped, so the unit's last messages never land on a browser that is going away.</para>
/// </summary>
public sealed class BlocksUnitWindow : Window
{
    private readonly string _unitId;
    private readonly Func<IUnit> _factory;
    private readonly IReadOnlyList<StrategyFile> _pageFiles;
    private readonly BlocksHost _host;
    private readonly IReadOnlyDictionary<string, object?>? _settings;
    private readonly string _pageRoot;
    private readonly WebUnitViewOptions? _viewOptions;
    private WebUnitView? _view;

    public BlocksUnitWindow(
        string unitId,
        string title,
        Func<IUnit> factory,
        IReadOnlyList<StrategyFile> pageFiles,
        BlocksHost host,
        IReadOnlyDictionary<string, object?>? settings = null,
        string? pageRoot = null,
        WebUnitViewOptions? viewOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        _unitId = unitId;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _pageFiles = pageFiles ?? [];
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings;
        _pageRoot = pageRoot ?? UnitPageFolder.DefaultRoot;
        _viewOptions = viewOptions;

        Title = title;
        Width = 1280;
        Height = 800;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x17));

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>The running unit's runtime, once the window has loaded.</summary>
    public BlocksUnitRuntime? Runtime { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            if (_pageFiles.Count > 0 && WebUnitView.RuntimeAvailable)
            {
                _view = new WebUnitView(_viewOptions);
                Content = _view;
                await _view.LoadAsync(UnitPageFolder.Write(_pageRoot, _unitId, _pageFiles));
            }
            else
            {
                Content = Notice(_pageFiles.Count == 0
                    ? "This unit has no page. It runs headless; its alerts and log lines appear in the terminal."
                    : "This unit's page needs the Microsoft Edge WebView2 Runtime, which is not installed.");
            }

            Runtime = new BlocksUnitRuntime(_factory, _unitId, _host, _settings, _view);
            await Runtime.StartAsync();
        }
        catch (Exception ex)
        {
            Content = Notice($"The unit could not start: {ex.Message}");
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;

        try
        {
            if (Runtime is not null) await Runtime.DisposeAsync();
        }
        finally
        {
            _view?.Dispose();
        }
    }

    private static TextBlock Notice(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)),
        Margin = new Thickness(24),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
    };
}
