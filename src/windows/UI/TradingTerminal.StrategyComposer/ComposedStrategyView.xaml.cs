using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Charts;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.OrderBook;
using TradingTerminal.UI;
using TradingTerminal.VolumeFootprint;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// The <b>composed default live window</b> for an authored strategy that shipped no view: the shared
/// strategy chrome (setup hero, param strip, chrome bar, signal feed) around the embeddable chart
/// panels selected by the descriptor's <see cref="ITradingStrategy.DataRequirement"/> — Bars gets the
/// price chart, Depth gets the order-book ladder + heatmap, TradeTape gets the volume footprint, all
/// with their Embedded presets (no toolbar, no ML). An L1-only strategy gets a live quote card.
/// <para>
/// The <c>DataContext</c> is the authored <see cref="LiveSignalStrategyViewModelBase"/>, assigned by
/// the strategy factory exactly as for a hand-written view; every frame binding rides the base class's
/// members, so any conforming authored view-model works unseen. The panels do NOT inherit it — each
/// gets its own tool view-model, created here with embed options so the forecasters are never built
/// and the standalone tools' persisted instrument picks are neither read nor overwritten.
/// </para>
/// <para>
/// The strategy window owns the instrument: the panels' toolbars are gated off, and this view pushes
/// the strategy's <c>SelectedInstrument</c> into each panel view-model when setup completes (and on
/// any later change). Pause is relayed the same way. Panel view-models are disposed when the host
/// window closes — the strategy view-model itself is disposed by the shell, like every strategy's.
/// </para>
/// </summary>
public partial class ComposedStrategyView : UserControl, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ITradingStrategy _descriptor;

    private readonly ChartsViewModel? _chartsVm;
    private readonly OrderBookViewModel? _bookVm;
    private readonly VolumeFootprintViewModel? _footprintVm;

    private LiveSignalStrategyViewModelBase? _strategyVm;
    private Window? _hostWindow;
    private string? _pushedInstrumentKey;
    private bool _disposed;

    public ComposedStrategyView(ITradingStrategy descriptor, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(services);
        _descriptor = descriptor;
        _services = services;

        InitializeComponent();

        var requirement = descriptor.DataRequirement;

        Setup.Title = descriptor.DisplayName;
        Setup.Subtitle = "AI-AUTHORED STRATEGY";
        Setup.Description = string.IsNullOrWhiteSpace(descriptor.Description)
            ? "Authored in the AI Strategy Builder. The host composed this live window from the data the strategy declares it consumes."
            : descriptor.Description;
        Setup.Tags = TagsFor(requirement);

        Chrome.SnapshotName = Sanitize(descriptor.Id);
        Chrome.HelpContent = BuildHelp(requirement);

        TapeStat.Visibility = requirement.HasFlag(StrategyDataRequirement.TradeTape)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The panels are created eagerly (with no instrument pinned yet) so ChartsPanel's WebView can
        // boot behind the setup form; nothing subscribes until the strategy's instrument is pushed.
        var panels = new List<(string Caption, FrameworkElement Panel)>();
        if (requirement.HasFlag(StrategyDataRequirement.Bars))
        {
            _chartsVm = ActivatorUtilities.CreateInstance<ChartsViewModel>(_services, new ChartsEmbedOptions());
            panels.Add(("PRICE · 1m", new ChartsPanel { Features = ChartsPanelFeatures.Embedded, DataContext = _chartsVm }));
        }
        if (requirement.HasFlag(StrategyDataRequirement.Depth))
        {
            _bookVm = ActivatorUtilities.CreateInstance<OrderBookViewModel>(_services, new OrderBookEmbedOptions());
            panels.Add(("ORDER BOOK · DEPTH", new OrderBookPanel { Features = OrderBookPanelFeatures.Embedded, DataContext = _bookVm }));
        }
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape))
        {
            _footprintVm = ActivatorUtilities.CreateInstance<VolumeFootprintViewModel>(_services, new VolumeFootprintEmbedOptions());
            panels.Add(("FOOTPRINT · TRADE TAPE", new VolumeFootprintPanel { Features = VolumeFootprintPanelFeatures.Embedded, DataContext = _footprintVm }));
        }
        BuildPanelGrid(panels);

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    /// <summary>The panels this composition holds, in display order — for tests and diagnostics.</summary>
    public IReadOnlyList<FrameworkElement> Panels { get; private set; } = [];

    // ── composition ─────────────────────────────────────────────────────────────────────────────────

    private void BuildPanelGrid(List<(string Caption, FrameworkElement Panel)> panels)
    {
        Panels = [.. panels.Select(p => p.Panel)];
        if (panels.Count == 0)
        {
            QuoteCard.Visibility = Visibility.Visible;
            return;
        }

        for (var i = 0; i < panels.Count; i++)
        {
            if (i > 0)
            {
                // Splitter column between panels.
                PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var splitter = new GridSplitter
                {
                    Width = 5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = System.Windows.Media.Brushes.Transparent,
                };
                Grid.SetColumn(splitter, PanelHost.ColumnDefinitions.Count - 1);
                PanelHost.Children.Add(splitter);
            }

            PanelHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 220,
            });

            var cell = new DockPanel();
            var caption = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock
                {
                    Text = panels[i].Caption,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            caption.SetResourceReference(Border.BorderBrushProperty, "Border.Primary");
            ((TextBlock)caption.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            DockPanel.SetDock(caption, Dock.Top);
            cell.Children.Add(caption);
            cell.Children.Add(panels[i].Panel);

            Grid.SetColumn(cell, PanelHost.ColumnDefinitions.Count - 1);
            PanelHost.Children.Add(cell);
        }
    }

    private static string TagsFor(StrategyDataRequirement requirement)
    {
        var tags = new List<string>();
        if (requirement.HasFlag(StrategyDataRequirement.L1)) tags.Add("L1");
        if (requirement.HasFlag(StrategyDataRequirement.Bars)) tags.Add("Bars");
        if (requirement.HasFlag(StrategyDataRequirement.Depth)) tags.Add("Depth");
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add("Trade tape");
        return string.Join(", ", tags);
    }

    private static TextBlock BuildHelp(StrategyDataRequirement requirement)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, LineHeight = 17 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        void Add(string heading, string body)
        {
            if (text.Inlines.Count > 0) text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run(heading) { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(" — " + body));
        }

        Add("Composed window", "this strategy was authored in the AI Strategy Builder and shipped no " +
            "view of its own, so the host built this one from the data it declares it consumes.");
        if (requirement.HasFlag(StrategyDataRequirement.Bars))
            Add("Price", "1-minute candles with indicators for the instrument the strategy is trading.");
        if (requirement.HasFlag(StrategyDataRequirement.Depth))
            Add("Order book", "the live depth ladder, microstructure strip and liquidity heatmap the " +
                "strategy's OnDepthAsync sees.");
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape))
            Add("Footprint", "bid×ask volume clusters built from the same trade prints the strategy's " +
                "OnTradeAsync consumes.");
        Add("Signals", "every order the kernel routes lands in the feed at the bottom — BUY/SELL, price " +
            "and note. Arm the algo (▶ Run) to mark them as live; this build never routes real orders.");
        Add("ML overlays", "off by design in embedded panels — a strategy window should not pay to " +
            "train the tools' forecasters. Open the standalone tool for the ML view.");
        return text;
    }

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    // ── strategy view-model wiring ──────────────────────────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_strategyVm is not null) _strategyVm.PropertyChanged -= OnStrategyPropertyChanged;
        _strategyVm = e.NewValue as LiveSignalStrategyViewModelBase;
        if (_strategyVm is null) return;

        _strategyVm.PropertyChanged += OnStrategyPropertyChanged;
        PushInstrument();
        SyncPause();
    }

    private void OnStrategyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveSignalStrategyViewModelBase.SelectedInstrument):
            case nameof(LiveSignalStrategyViewModelBase.IsConfigured):
                PushInstrument();
                break;
            case nameof(LiveSignalStrategyViewModelBase.IsPaused):
                SyncPause();
                break;
        }
    }

    /// <summary>Points every panel at the strategy's instrument — once setup is complete, and again on
    /// any later change. Keyed by symbol+broker so re-assignments of an equal pick don't restart the
    /// panels' streams.</summary>
    private void PushInstrument()
    {
        if (_strategyVm is not { IsConfigured: true, SelectedInstrument: { } instrument }) return;

        var key = $"{instrument.Contract.Symbol}|{instrument.Broker}";
        if (key == _pushedInstrumentKey) return;
        _pushedInstrumentKey = key;

        if (_bookVm is not null) _bookVm.SelectedInstrument = instrument;
        if (_footprintVm is not null) _footprintVm.SelectedInstrument = instrument;
        if (_chartsVm is not null)
            _chartsVm.SelectedInstrument = new TradableInstrument(
                instrument.DisplayName, instrument.Category, instrument.Contract,
                instrument.Broker ?? FallbackBroker());
    }

    /// <summary>The chart view-model needs a concrete broker on its instrument; when the strategy's
    /// pick doesn't pin one, prefer whatever is connected (the chart's own resolve re-checks anyway).</summary>
    private BrokerKind FallbackBroker()
    {
        var selector = _services.GetService<IBrokerSelector>();
        return selector is { Connected.Count: > 0 } s ? s.Connected[0] : BrokerKind.Simulated;
    }

    private void SyncPause()
    {
        if (_strategyVm is null) return;
        var paused = _strategyVm.IsPaused;
        if (_bookVm is not null) _bookVm.IsPaused = paused;
        if (_footprintVm is not null) _footprintVm.IsPaused = paused;
        if (_chartsVm is not null) _chartsVm.IsPaused = paused;
    }

    // ── lifetime ────────────────────────────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) return;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.Closed += OnHostWindowClosed;
    }

    private void OnHostWindowClosed(object? sender, EventArgs e) => Dispose();

    /// <summary>Drops the panel view-models (and with them their hub subscriptions, render timers and
    /// channels). The authored strategy view-model is NOT disposed here — the shell owns it, exactly as
    /// for a hand-written strategy window. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hostWindow is not null)
        {
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow = null;
        }
        if (_strategyVm is not null)
        {
            _strategyVm.PropertyChanged -= OnStrategyPropertyChanged;
            _strategyVm = null;
        }
        _chartsVm?.Dispose();
        _bookVm?.Dispose();
        _footprintVm?.Dispose();
    }
}
