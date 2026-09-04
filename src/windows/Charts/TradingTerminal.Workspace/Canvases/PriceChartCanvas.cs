using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Charts;

namespace TradingTerminal.Workspace.Canvases;

/// <summary>
/// The price chart, as a canvas.
///
/// <para>Almost no new code, which is the point: <see cref="ChartsPanel"/> was already built to be
/// embedded with its chrome switched off — <see cref="ChartsPanelFeatures"/> exists for exactly that
/// and <c>ComposedStrategyView</c> already uses it. The only thing added here is the wire from the
/// shell's selection down into the chart's own.</para>
///
/// <para><b>It does NOT use <c>ChartsEmbedOptions</c>.</b> That mode pins the instrument at
/// construction and returns early without loading a universe, because it was built for a strategy
/// window that owns the symbol for the life of the panel. The workspace changes symbol while the
/// panel is alive, so the chart is built in its ordinary mode and driven instead.</para>
/// </summary>
public static class PriceChartCanvas
{
    public const string CanvasId = "price-chart";

    public static WorkspaceCanvas Descriptor { get; } = new(
        CanvasId,
        "Price chart",
        "Charts",
        Create);

    private static WorkspaceCanvasView Create(WorkspaceCanvasContext context)
    {
        var model = context.Services.GetRequiredService<ChartsViewModel>();

        // Toolbar and status off: the shell is showing both, and two instrument pickers arguing over
        // one chart is the exact duplication this shell exists to remove. The options rail stays,
        // but as the canvas's contribution to the shell's rail rather than its own column.
        var panel = new ChartsPanel
        {
            Features = ChartsPanelFeatures.Embedded with { OptionsRail = false },
            DataContext = model,
        };

        var link = new SubjectLink(context.Subject, model);

        return new WorkspaceCanvasView(panel, OptionsRail: null) { Lifetime = link };
    }

    /// <summary>
    /// Pushes the shell's selection into the chart, and stops when the canvas goes away.
    ///
    /// <para>A small class rather than a lambda because the subscription has to be undone: the shell's
    /// subject outlives every canvas, so a canvas that subscribed and never unsubscribed would keep a
    /// disposed chart alive and keep feeding it — and on a WebView2 that means a browser told to
    /// render after its host is gone.</para>
    /// </summary>
    private sealed class SubjectLink : IDisposable
    {
        private readonly WorkspaceSubject _subject;
        private readonly ChartsViewModel _chart;

        public SubjectLink(WorkspaceSubject subject, ChartsViewModel chart)
        {
            _subject = subject;
            _chart = chart;
            _subject.PropertyChanged += OnSubjectChanged;
            Apply();
        }

        private void OnSubjectChanged(object? sender, PropertyChangedEventArgs e) => Apply();

        private void Apply()
        {
            if (_subject.Instrument is { } instrument) _chart.SelectedInstrument = instrument;
            if (_subject.Timeframe is { } timeframe) _chart.SelectedTimeframe = timeframe;
        }

        public void Dispose()
        {
            _subject.PropertyChanged -= OnSubjectChanged;
            _chart.Dispose();
        }
    }
}
