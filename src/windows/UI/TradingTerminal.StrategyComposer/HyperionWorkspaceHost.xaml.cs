using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Charts;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.OrderBook;
using TradingTerminal.UI;
using TradingTerminal.VolumeFootprint;

namespace TradingTerminal.StrategyComposer;

/// <summary>
/// Hyperion Workspace: docks the same embedded Charts / OrderBook / Footprint panels the composed
/// live window uses — Horizon-style context next to Prove, without inventing new tools.
/// </summary>
public partial class HyperionWorkspaceHost : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly IServiceProvider _services;
    private readonly ChartsViewModel _chartsVm;
    private readonly OrderBookViewModel _bookVm;
    private readonly VolumeFootprintViewModel _footprintVm;

    private IReadOnlyList<SignalInstrument> _all = [];
    private string _instrumentSearchText = string.Empty;
    private SignalInstrument? _selectedInstrument;
    private string? _pushedKey;
    private bool _disposed;

    public HyperionWorkspaceHost(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        InitializeComponent();

        _chartsVm = ActivatorUtilities.CreateInstance<ChartsViewModel>(_services, new ChartsEmbedOptions());
        _bookVm = ActivatorUtilities.CreateInstance<OrderBookViewModel>(_services, new OrderBookEmbedOptions());
        _footprintVm = ActivatorUtilities.CreateInstance<VolumeFootprintViewModel>(_services, new VolumeFootprintEmbedOptions());

        BuildPanels();
        _ = LoadInstrumentsAsync();
        Unloaded += (_, _) => Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InstrumentSearchText
    {
        get => _instrumentSearchText;
        set
        {
            if (_instrumentSearchText == value) return;
            _instrumentSearchText = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstrumentSearchText)));
            RefreshFilter();
        }
    }

    public ObservableCollection<SignalInstrument> FilteredInstruments { get; } = [];

    public SignalInstrument? SelectedInstrument
    {
        get => _selectedInstrument;
        set
        {
            if (ReferenceEquals(_selectedInstrument, value)) return;
            _selectedInstrument = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedInstrument)));
            PushInstrument();
        }
    }

    private void BuildPanels()
    {
        var panels = new (string Caption, FrameworkElement Panel)[]
        {
            ("PRICE · 1m", new ChartsPanel { Features = ChartsPanelFeatures.Embedded, DataContext = _chartsVm }),
            ("ORDER BOOK · DEPTH", new OrderBookPanel { Features = OrderBookPanelFeatures.Embedded, DataContext = _bookVm }),
            ("FOOTPRINT · TAPE", new VolumeFootprintPanel { Features = VolumeFootprintPanelFeatures.Embedded, DataContext = _footprintVm }),
        };

        for (var i = 0; i < panels.Length; i++)
        {
            if (i > 0)
            {
                PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var splitter = new GridSplitter
                {
                    Width = 4,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                };
                Grid.SetColumn(splitter, PanelHost.ColumnDefinitions.Count - 1);
                PanelHost.Children.Add(splitter);
            }

            PanelHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new DockPanel();
            var caption = new TextBlock
            {
                Text = panels[i].Caption,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            DockPanel.SetDock(caption, Dock.Top);
            cell.Children.Add(caption);
            cell.Children.Add(panels[i].Panel);
            Grid.SetColumn(cell, PanelHost.ColumnDefinitions.Count - 1);
            PanelHost.Children.Add(cell);
        }
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var repo = _services.GetRequiredService<IMarketDataRepository>();
            var registry = _services.GetRequiredService<IInstrumentRegistry>();
            var logger = _services.GetService<ILoggerFactory>()?.CreateLogger("HyperionWorkspace");
            _all = await BrokerInstrumentUniverse.LoadAsync(repo, registry, logger: logger).ConfigureAwait(true);
            if (_all.Count == 0)
                _all = SignalInstrumentCatalog.All;
        }
        catch
        {
            _all = SignalInstrumentCatalog.All;
        }

        RefreshFilter();
        if (SelectedInstrument is null && FilteredInstruments.Count > 0)
            SelectedInstrument = FilteredInstruments[0];
    }

    private void RefreshFilter()
    {
        FilteredInstruments.Clear();
        foreach (var row in InstrumentPickerFilter.Visible(_all, InstrumentSearchText, SelectedInstrument, 200))
            FilteredInstruments.Add(row);
    }

    private void PushInstrument()
    {
        if (SelectedInstrument is not { } instrument) return;
        var key = $"{instrument.Contract.Symbol}|{instrument.Broker}";
        if (key == _pushedKey) return;
        _pushedKey = key;

        _bookVm.SelectedInstrument = instrument;
        _footprintVm.SelectedInstrument = instrument;
        _chartsVm.SelectedInstrument = new TradableInstrument(
            instrument.DisplayName, instrument.Category, instrument.Contract,
            instrument.Broker ?? FallbackBroker());
        RefreshFilter();
    }

    private BrokerKind FallbackBroker()
    {
        var selector = _services.GetService<IBrokerSelector>();
        return selector is { Connected.Count: > 0 } s ? s.Connected[0] : BrokerKind.Simulated;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_chartsVm as IDisposable)?.Dispose();
        (_bookVm as IDisposable)?.Dispose();
        (_footprintVm as IDisposable)?.Dispose();
    }
}
