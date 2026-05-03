using System.Windows;
using System.Windows.Controls;
using ScottPlot;
using ScottPlot.Plottables;
using Bar = TradingTerminal.Core.Domain.Bar;

namespace TradingTerminal.Strategies.Example;

public partial class ExampleStrategyView : UserControl
{
    private CandlestickPlot? _candles;
    private Annotation? _lastPriceAnnotation;
    private ExampleStrategyViewModel? _vm;

    public ExampleStrategyView()
    {
        InitializeComponent();
        ConfigureChart();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void ConfigureChart()
    {
        var plot = Plot.Plot;

        var bg     = System.Drawing.Color.FromArgb(30, 30, 30);
        var grid   = System.Drawing.Color.FromArgb(63, 63, 70);
        var text   = System.Drawing.Color.FromArgb(220, 220, 220);

        plot.FigureBackground.Color = ScottPlot.Color.FromARGB((uint)bg.ToArgb());
        plot.DataBackground.Color   = ScottPlot.Color.FromARGB((uint)bg.ToArgb());
        plot.Axes.Color(ScottPlot.Color.FromARGB((uint)text.ToArgb()));
        plot.Grid.MajorLineColor    = ScottPlot.Color.FromARGB((uint)grid.ToArgb());

        plot.Axes.DateTimeTicksBottom();
        plot.Title("");
        Plot.Refresh();
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.BarsChanged -= OnBarsChanged;
        _vm = e.NewValue as ExampleStrategyViewModel;
        if (_vm is not null)
        {
            _vm.BarsChanged += OnBarsChanged;
            RedrawChart();
        }
    }

    private void OnBarsChanged(object? sender, EventArgs e) => RedrawChart();

    private void RedrawChart()
    {
        if (_vm is null) return;

        var plot = Plot.Plot;
        plot.Clear();

        if (_vm.Bars.Count == 0)
        {
            Plot.Refresh();
            return;
        }

        var ohlcs = _vm.Bars.Select(ToOhlc).ToList();
        _candles = plot.Add.Candlestick(ohlcs);
        _candles.RisingColor  = ScottPlot.Color.FromHex("#26A69A");
        _candles.FallingColor = ScottPlot.Color.FromHex("#EF5350");

        if (_vm.LastPrice is { } lp)
        {
            _lastPriceAnnotation = plot.Add.Annotation($"Last  {lp:F2}", Alignment.UpperRight);
            _lastPriceAnnotation.LabelFontColor = ScottPlot.Color.FromHex("#DCDCDC");
            _lastPriceAnnotation.LabelBackgroundColor = ScottPlot.Color.FromHex("#2D2D30");
            _lastPriceAnnotation.LabelBorderColor = ScottPlot.Color.FromHex("#3F3F46");
        }

        plot.Axes.AutoScale();
        Plot.Refresh();
    }

    private static OHLC ToOhlc(Bar bar) =>
        new(bar.Open, bar.High, bar.Low, bar.Close, bar.TimestampUtc, BarSpan(bar));

    private static TimeSpan BarSpan(Bar bar)
    {
        // Best-effort estimate; chart widths are visual-only.
        _ = bar;
        return TimeSpan.FromMinutes(3);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.BarsChanged -= OnBarsChanged;
    }
}
