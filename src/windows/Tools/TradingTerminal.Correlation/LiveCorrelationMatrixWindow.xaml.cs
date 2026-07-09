using System;
using System.Windows;
using System.Windows.Data;
using MahApps.Metro.Controls;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.Correlation;

/// <summary>
/// Standalone window hosting the Live Correlation Matrix tool. Pure view — all behaviour lives in
/// <see cref="LiveCorrelationMatrixViewModel"/>, assigned as the DataContext by the opener.
/// The matrix renderer is constructed here rather than in XAML because same-project element
/// types don't resolve in MarkupCompilePass2 (same quirk the login window works around).
/// </summary>
public partial class LiveCorrelationMatrixWindow : MetroWindow
{
    public LiveCorrelationMatrixWindow()
    {
        InitializeComponent();

        var matrix = new CorrelationMatrixControl();
        matrix.SetBinding(CorrelationMatrixControl.MatrixProperty,
            new Binding(nameof(LiveCorrelationMatrixViewModel.MatrixResult)));
        MatrixHost.Content = matrix;

        // Dispose the VM when the window closes — critical for the live tool: otherwise its hub quote
        // subscriptions and sampler DispatcherTimer keep the VM rooted (and ticking) forever.
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        ViewExport.SavePng(root, $"correlation-{DateTime.Now:yyyyMMdd-HHmmss}");
    }
}
