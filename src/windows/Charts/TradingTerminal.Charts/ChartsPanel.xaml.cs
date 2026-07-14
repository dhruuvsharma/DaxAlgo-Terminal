using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace TradingTerminal.Charts;

/// <summary>
/// The price chart, as an embeddable control: the Lightweight Charts page in a WebView2, plus the toolbar,
/// the ⚙ indicator rail and the status line the standalone window shows — each switchable via
/// <see cref="Features"/>.
/// <para>
/// The window is now a thin host around this. An authored strategy embeds it too, usually with the chrome
/// off: a panel is only as heavy as the features it is asked for.
/// </para>
/// <para>
/// Pure view-bridge: it owns the WebView2 lifecycle (virtual-host mapping to the bundled
/// <c>Charts/Assets</c> folder so the page loads fully offline) and forwards <see cref="ChartsViewModel"/>
/// events to JS via <c>ExecuteScriptAsync</c>. No business logic lives here (MVVM rule).
/// </para>
/// </summary>
public partial class ChartsPanel : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Which parts of the panel are switched on. Set it before the control loads (it is read
    /// once, on load, to decide what to build and what the view-model should bother computing).</summary>
    public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
        nameof(Features), typeof(ChartsPanelFeatures), typeof(ChartsPanel),
        new PropertyMetadata(ChartsPanelFeatures.Full));

    public ChartsPanelFeatures Features
    {
        get => (ChartsPanelFeatures)GetValue(FeaturesProperty);
        set => SetValue(FeaturesProperty, value);
    }

    private ChartsViewModel? _vm;
    private bool _ready;
    private bool _started;
    private Window? _host;

    public ChartsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as ChartsViewModel;
        ApplyFeatureGates();

        // Loaded fires again on every re-parent (a tab switch, say). The WebView2 must be booted — and
        // torn down — exactly once, so hang teardown off the owning window's close, not off Unloaded:
        // unloading a tab must not destroy the browser the user is coming back to.
        if (_started) return;
        _started = true;
        _host = Window.GetWindow(this);
        if (_host is not null) _host.Closed += OnHostClosed;

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

    /// <summary>
    /// Pushes the build-time gates down onto the view-model's user toggles. The view-model computes an
    /// indicator only when its toggle is on, so switching them off here is what actually skips the work —
    /// collapsing the rail alone would leave four series being computed per bar for nobody.
    /// </summary>
    private void ApplyFeatureGates()
    {
        // The rail is normally driven by the toolbar's gear toggle — with the toolbar gated off there is
        // nothing left to close it with, so honour the feature gate directly.
        if (!Features.OptionsRail || !Features.Toolbar) OptionsToggle.IsChecked = Features.OptionsRail;

        if (_vm is null || Features.Indicators) return;
        _vm.ShowSma = false;
        _vm.ShowEma = false;
        _vm.ShowRsi = false;
        _vm.ShowMacd = false;
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

    private void OnSnapshotReady(object? sender, ChartSnapshot snapshot)
    {
        Push("window.dax.setData", snapshot);
        // Empty state renders inside the page (WebView2 airspace hides WPF overlays).
        Push("window.dax.message", snapshot.Candles.Length == 0
            ? $"No history for {snapshot.Symbol} ({snapshot.Timeframe})\n" +
              "Connect a broker and stream this instrument, or pick another one.\n" +
              "Every broker serves bars — the Simulated broker always works offline."
            : "");
    }

    private void OnCandleUpdated(object? sender, ChartCandle candle) =>
        Push("window.dax.updateCandle", candle);

    /// <summary>PNG snapshot. WebView2 composits out-of-process, so <c>RenderTargetBitmap</c> (and
    /// therefore the shared <c>ViewExport.SavePng</c>) captures a blank rectangle — the browser's own
    /// <see cref="CoreWebView2.CapturePreviewAsync"/> is the only correct path.</summary>
    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is null) return;
        var symbol = _vm?.SelectedInstrument?.Contract.Symbol ?? "chart";
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"chart-{symbol}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using (var stream = File.Create(dialog.FileName))
                await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            if (_vm is not null) _vm.Status = $"Snapshot saved → {dialog.FileName}";
        }
        catch (Exception ex)
        {
            if (_vm is not null) _vm.Status = $"Snapshot failed: {ex.Message}";
        }
    }

    private void Push(string fn, object payload)
    {
        if (Web.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        // Fire-and-forget; ExecuteScriptAsync marshals onto the WebView2 UI thread internally.
        _ = Web.CoreWebView2.ExecuteScriptAsync($"{fn}({json})");
    }

    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (_host is not null) _host.Closed -= OnHostClosed;
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
