using System;
using MahApps.Metro.Controls;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>
/// View for the Index Regime Graph. The body is a static regime heatmap table bound straight to the
/// view-model — no scoring or layout logic lives here. The only code-behind responsibility is
/// stopping the analysis loop and disposing the view-model when the window closes.
/// </summary>
public partial class IndexRegimeGraphWindow : MetroWindow
{
    private IndexRegimeGraphViewModel? _vm;

    public IndexRegimeGraphWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => _vm = e.NewValue as IndexRegimeGraphViewModel;
        Closed += OnClosed;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        try { await _vm.StopCommand.ExecuteAsync(null); } catch { /* shutting down */ }
        _vm.Dispose();
    }
}
