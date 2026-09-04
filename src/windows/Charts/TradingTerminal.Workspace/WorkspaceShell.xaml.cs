using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.Workspace;

/// <summary>
/// View plumbing for the workspace shell: swap the canvas when the selection changes, and put its
/// options rail in the rail slot.
///
/// <para>Everything else is binding. The code-behind exists because a canvas is a
/// <see cref="FrameworkElement"/> produced by a factory rather than a DataTemplate — a canvas may be
/// a WebView2 host, a Helix viewport or a render surface, and templating those from XAML would mean
/// the shell knowing all of their types.</para>
/// </summary>
public partial class WorkspaceShell : UserControl
{
    private WorkspaceViewModel? _model;
    private WorkspaceCanvasView? _current;

    public WorkspaceShell(IServiceProvider services)
    {
        Services = services;
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        OptionsToggle.Checked += OnRailToggled;
        OptionsToggle.Unchecked += OnRailToggled;
        Unloaded += (_, _) => Detach();
    }

    /// <summary>The composition root, handed to every canvas so it can resolve what it needs without
    /// the shell knowing what that is.</summary>
    public IServiceProvider Services { get; }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;

        _model = DataContext as WorkspaceViewModel;
        if (_model is null) return;

        _model.PropertyChanged += OnModelChanged;
        ShowCanvas(_model.SelectedCanvas);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.SelectedCanvas)) ShowCanvas(_model?.SelectedCanvas);
    }

    /// <summary>
    /// Put one canvas in the centre and take the previous one down.
    ///
    /// <para><b>The teardown is not tidiness, it is correctness.</b> The price chart owns a WebView2,
    /// and WebView2 composes out of process — its output is painted above any WPF content sharing the
    /// cell. Leaving a chart realised behind another canvas therefore paints the browser straight over
    /// whatever replaced it. Disposing on the way out is what keeps the swap a swap.</para>
    /// </summary>
    private void ShowCanvas(WorkspaceCanvas? canvas)
    {
        _current?.Lifetime?.Dispose();
        _current = null;

        CanvasHost.Content = null;
        RailContent.Content = null;
        RailHost.Visibility = Visibility.Collapsed;

        if (canvas is null || _model is null) return;

        var view = canvas.Create(new WorkspaceCanvasContext(Services, _model.Subject));
        _current = view;

        CanvasHost.Content = view.View;

        RailContent.Content = view.OptionsRail;
        UpdateRail();
    }

    /// <summary>The rail shows only when the ⚙ is on AND the current canvas actually brought one — a
    /// canvas with no options must not leave an empty 200px column behind.</summary>
    private void UpdateRail() =>
        RailHost.Visibility = OptionsToggle.IsChecked == true && RailContent.Content is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnRailToggled(object sender, RoutedEventArgs e) => UpdateRail();

    private void Detach()
    {
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _current?.Lifetime?.Dispose();
        _current = null;
    }
}
