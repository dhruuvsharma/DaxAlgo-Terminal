using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;

namespace TradingTerminal.UI.Catalog;

/// <summary>
/// Portable view-model for the strategy catalog. It is a plain <see cref="ObservableObject"/> over
/// the broker-neutral <see cref="BacktestStrategyOption"/> list from the headless catalog, with no
/// UI-framework types. An optional <c>onLog</c> callback lets the host route selection activity to
/// the universal Activity Log.
/// </summary>
public sealed partial class StrategyCatalogViewModel : ObservableObject
{
    private readonly Action<string>? _onLog;

    public StrategyCatalogViewModel(IEnumerable<BacktestStrategyOption> options, Action<string>? onLog = null)
    {
        _onLog = onLog;
        Items = new ObservableCollection<StrategyCatalogItem>(
            options.Select(o => new StrategyCatalogItem(
                o.Id,
                o.DisplayName,
                o.Schema?.Parameters.Count ?? 0,
                o.Fast)));
        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<StrategyCatalogItem> Items { get; }

    public int Count => Items.Count;

    [ObservableProperty]
    private StrategyCatalogItem? _selectedItem;

    /// <summary>Human-readable detail block for the currently selected strategy.</summary>
    public string Details => SelectedItem is { } s
        ? $"Id:          {s.Id}\n" +
          $"Name:        {s.DisplayName}\n" +
          $"Parameters:  {s.ParameterCount}\n" +
          $"GPU-fast:    {(s.Fast ? "yes" : "no")}"
        : "Select a strategy to see its details.";

    partial void OnSelectedItemChanged(StrategyCatalogItem? value)
    {
        OnPropertyChanged(nameof(Details));
        if (value is not null) _onLog?.Invoke($"Selected strategy '{value.DisplayName}' ({value.Id}).");
    }
}

/// <summary>One catalog row — a flat, portable projection of a headless strategy option.</summary>
public sealed record StrategyCatalogItem(string Id, string DisplayName, int ParameterCount, bool Fast);
