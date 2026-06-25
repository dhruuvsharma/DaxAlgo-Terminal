using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>
/// Header-strip API meter — one chip per broker being talked to. Avalonia mirror of the WPF
/// <c>BrokerApiMeterViewModel</c>: polls <see cref="IBrokerApiMeter"/> on a 1 Hz timer and updates
/// chip counters in place. The only difference from the WPF VM is the timer
/// (<see cref="Avalonia.Threading.DispatcherTimer"/> instead of the WPF one).
/// </summary>
public sealed partial class BrokerApiMeterViewModel : ViewModelBase, IDisposable
{
    private readonly IBrokerApiMeter _meter;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<BrokerKind, BrokerApiChipViewModel> _chipsByBroker = new();

    public BrokerApiMeterViewModel(IBrokerApiMeter meter)
    {
        _meter = meter;
        Chips = new ObservableCollection<BrokerApiChipViewModel>();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<BrokerApiChipViewModel> Chips { get; }

    [ObservableProperty] private BrokerApiChipStatus _overallStatus = BrokerApiChipStatus.Untracked;
    [ObservableProperty] private long _totalCalls;
    [ObservableProperty] private int _trackedBrokerCount;
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private string _summaryText = "No API activity yet";

    private void Refresh()
    {
        var snapshot = _meter.Snapshot();
        foreach (var usage in snapshot)
        {
            if (!_chipsByBroker.TryGetValue(usage.Broker, out var chip))
            {
                chip = new BrokerApiChipViewModel(usage.Broker);
                _chipsByBroker[usage.Broker] = chip;
                Chips.Add(chip);
            }
            chip.TotalCalls = usage.TotalCalls;
            chip.CallsLastMinute = usage.CallsLastMinute;
            chip.SoftLimitPerMinute = usage.SoftLimitPerMinute;
        }

        long total = 0;
        var worst = BrokerApiChipStatus.Untracked;
        foreach (var chip in Chips)
        {
            total += chip.TotalCalls;
            if (chip.Status > worst) worst = chip.Status;
        }
        TotalCalls = total;
        TrackedBrokerCount = Chips.Count;
        OverallStatus = worst;
        HasActivity = Chips.Count > 0;
        SummaryText = Chips.Count == 0
            ? "No API activity yet"
            : $"{Chips.Count} broker{(Chips.Count == 1 ? "" : "s")} · {total:n0} calls this session";
    }

    public void Dispose() => _timer.Stop();
}
