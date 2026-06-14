using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.App.Shell;

/// <summary>One scrolling ticker cell: last price, %change since the tape opened, and the last tick's
/// direction (for the arrow/colour).</summary>
public sealed partial class TickerItem : ObservableObject
{
    public required string Symbol { get; init; }

    private double _open;

    [ObservableProperty] private double _last;
    [ObservableProperty] private double _changePercent;
    [ObservableProperty] private int _direction; // +1 up tick, -1 down tick, 0 flat

    public void Update(double price)
    {
        if (price <= 0) return;
        if (_open <= 0) _open = price;
        Direction = price > Last ? 1 : price < Last ? -1 : Direction;
        Last = price;
        ChangePercent = _open > 0 ? (price - _open) / _open * 100.0 : 0;
    }
}

/// <summary>
/// Drives the header ticker tape. Pulls the connected brokers' instrument universe from
/// <see cref="IMarketDataRepository"/>, subscribes to L1 for a bounded set, and updates each cell as
/// ticks arrive (the repository already marshals ticks onto the UI thread, so no dispatcher juggling
/// here). Re-binds whenever broker connectivity changes. Purely a display feed — data only.
/// </summary>
public sealed partial class TickerTapeViewModel : ViewModelBase, IDisposable
{
    private const int MaxSymbols = 16;

    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<TickerTapeViewModel> _logger;
    private readonly Dictionary<string, TickerItem> _byKey = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;

    public TickerTapeViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<TickerTapeViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Items = new ObservableCollection<TickerItem>();

        _selector.StateChanged += OnBrokerStateChanged;
        Restart();
    }

    public ObservableCollection<TickerItem> Items { get; }

    [ObservableProperty] private bool _hasItems;

    private void OnBrokerStateChanged(object? sender, BrokerStateChangedEventArgs e)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.BeginInvoke(new Action(Restart));
        else
            Restart();
    }

    private void Restart()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_selector.Connected.Count == 0)
            {
                Items.Clear();
                _byKey.Clear();
                HasItems = false;
                return;
            }

            var universe = await _repository.ListInstrumentsAsync(ct);
            var picked = universe.Take(MaxSymbols).ToList();

            Items.Clear();
            _byKey.Clear();
            foreach (var ins in picked)
            {
                var item = new TickerItem { Symbol = ins.Contract.Symbol };
                _byKey[Key(ins)] = item;
                Items.Add(item);
            }
            HasItems = Items.Count > 0;

            await Task.WhenAll(picked.Select(ins => PumpAsync(ins, ct)));
        }
        catch (OperationCanceledException) { /* restart / shutdown */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Ticker tape run failed"); }
    }

    private async Task PumpAsync(TradableInstrument ins, CancellationToken ct)
    {
        if (!_byKey.TryGetValue(Key(ins), out var item)) return;
        try
        {
            await foreach (var tick in _repository.SubscribeTicksAsync(ins.Contract, ins.Broker, ct))
            {
                var mid = tick.Bid > 0 && tick.Ask > 0 ? (tick.Bid + tick.Ask) / 2.0 : Math.Max(tick.Bid, tick.Ask);
                item.Update(mid);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Ticker pump failed for {Symbol}", item.Symbol); }
    }

    private static string Key(TradableInstrument ins) => $"{ins.Broker}:{ins.Contract.Symbol}";

    public void Dispose()
    {
        _selector.StateChanged -= OnBrokerStateChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
