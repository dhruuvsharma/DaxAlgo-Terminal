using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// Hosts an authored strategy or visualizer: parameters, the author's own picture, the virtual book
/// and the activity log.
///
/// <para>The code-behind exists for three things the view cannot express: pointing the render surface
/// at the presenter's frame callback, following the log to the newest line, and resolving theme roles
/// from the application's brushes so an author names <c>Bullish</c> and gets whatever the current
/// theme uses.</para>
/// </summary>
public partial class AuthoredUnitView : UserControl, IDisposable
{
    private AuthoredUnitPresenter? _presenter;
    private int _disposed;

    public AuthoredUnitView()
    {
        InitializeComponent();
        Surface.ThemeResolver = ResolveTheme;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (Volatile.Read(ref _disposed) != 0 || e.NewValue is not AuthoredUnitPresenter presenter)
            return;

        _presenter = presenter;
        _presenter.PropertyChanged += OnPresenterChanged;
        _presenter.Log.CollectionChanged += OnLogChanged;
        Surface.Draw = presenter.Draw;
    }

    private void Detach()
    {
        if (_presenter is null)
            return;

        _presenter.PropertyChanged -= OnPresenterChanged;
        _presenter.Log.CollectionChanged -= OnLogChanged;
        _presenter = null;
        Surface.Draw = null;
    }

    private void OnPresenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_presenter is null)
            return;

        if (e.PropertyName == nameof(AuthoredUnitPresenter.Draw))
            Surface.Draw = _presenter.Draw;
    }

    /// <summary>
    /// Follows the newest line. A log that does not follow is worse than none during a live session:
    /// the interesting line is always the last one, and a user watching a running unit should not
    /// have to scroll to see it.
    /// </summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScroller.ScrollToEnd();
    }

    /// <summary>
    /// Turns a theme role into the colour the application is actually using, so a visualizer never
    /// names a literal and cannot paint itself invisible against the current background.
    /// </summary>
    private Color ResolveTheme(RenderThemeColor token)
    {
        var key = token switch
        {
            RenderThemeColor.Text => "Text.Primary",
            RenderThemeColor.TextSecondary => "Text.Secondary",
            RenderThemeColor.Background => "Background.Primary",
            RenderThemeColor.Surface => "Background.Elevated",
            RenderThemeColor.Grid => "Border.Brush",
            RenderThemeColor.Border => "Border.Strong",
            RenderThemeColor.Accent => "Accent.Brush",
            RenderThemeColor.Bullish => "Bullish.Brush",
            RenderThemeColor.Bearish => "Bearish.Brush",
            RenderThemeColor.Warning => "Warning.Brush",
            _ => "Text.Secondary",
        };

        return TryFindResource(key) is SolidColorBrush brush
            ? brush.Color
            : Colors.Gray;
    }

    /// <summary>Requests a repaint. Called by the runtime when the unit has new state to show.</summary>
    public void Invalidate()
    {
        if (Volatile.Read(ref _disposed) == 0)
            Surface.InvalidateVisual();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Detach();
        DataContextChanged -= OnDataContextChanged;
    }
}
