using System.IO;
using System.Text.Json;
using System.Windows;
using MahApps.Metro.Controls;
using Microsoft.Web.WebView2.Core;

namespace TradingTerminal.Charts;

/// <summary>
/// Hosts the Lightweight Charts page in a WebView2. Pure view-bridge: it owns the WebView2 lifecycle
/// (virtual-host mapping to the bundled <c>Charts/Assets</c> folder so the page loads fully offline)
/// and forwards <see cref="ChartsViewModel"/> events to JS via <c>ExecuteScriptAsync</c>. No business
/// logic lives here (MVVM rule).
/// </summary>
public partial class ChartsWindow : MetroWindow
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private ChartsViewModel? _vm;
    private bool _ready;

    public ChartsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as ChartsViewModel;
        try
        {
            await Web.EnsureCoreWebView2Async();
            var assets = Path.Combine(AppContext.BaseDirectory, "Charts", "Assets");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "charts.local", assets, CoreWebView2HostResourceAccessKind.Deny);
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
#if !DEBUG
            Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#endif
            Web.CoreWebView2.Navigate("https://charts.local/index.html");
        }
        catch (Exception ex)
        {
            if (_vm is not null)
                _vm.Status = "WebView2 runtime unavailable — install the Microsoft Edge WebView2 Runtime. " + ex.Message;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { return; }
        if (msg != "ready" || _ready || _vm is null) return;
        _ready = true;

        _vm.SnapshotReady += OnSnapshotReady;
        _vm.CandleUpdated += OnCandleUpdated;
        _ = _vm.NotifyChartReadyAsync();
    }

    private void OnSnapshotReady(object? sender, ChartSnapshot snapshot) =>
        Push("window.dax.setData", snapshot);

    private void OnCandleUpdated(object? sender, ChartCandle candle) =>
        Push("window.dax.updateCandle", candle);

    private void Push(string fn, object payload)
    {
        if (Web.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        // Fire-and-forget; ExecuteScriptAsync marshals onto the WebView2 UI thread internally.
        _ = Web.CoreWebView2.ExecuteScriptAsync($"{fn}({json})");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SnapshotReady -= OnSnapshotReady;
            _vm.CandleUpdated -= OnCandleUpdated;
        }
        if (Web.CoreWebView2 is not null)
            Web.CoreWebView2.WebMessageReceived -= OnWebMessage;
        Web.Dispose();
    }
}
