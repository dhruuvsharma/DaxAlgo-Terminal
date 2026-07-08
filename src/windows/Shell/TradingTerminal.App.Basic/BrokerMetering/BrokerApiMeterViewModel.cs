using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI;

namespace TradingTerminal.App.BrokerMetering;

/// <summary>
/// Header-strip widget showing one chip per broker that's currently being talked to. Polls
/// <see cref="IBrokerApiMeter"/> on a 1 Hz <see cref="DispatcherTimer"/> and updates the matching
/// chip's counters in place (no rebuild — keeps the chips from flickering or reordering).
///
/// Chips appear lazily: the first call recorded against a broker creates its chip; subsequent
/// calls only update counters. Chips never disappear during the session — once a broker has been
/// touched, its row stays for context (so you can see "I made 4k IB calls earlier and just stopped").
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

    /// <summary>One chip per broker that has had at least one call recorded. Iteration order
    /// matches the <see cref="BrokerKind"/> enum so chips don't shuffle between refreshes.</summary>
    public ObservableCollection<BrokerApiChipViewModel> Chips { get; }

    /// <summary>Worst (hottest) status across all tracked brokers — drives the dropdown button's
    /// accent so the header signals API health at a glance without opening the panel.</summary>
    [ObservableProperty] private BrokerApiChipStatus _overallStatus = BrokerApiChipStatus.Untracked;

    /// <summary>Total API calls recorded this session across every broker.</summary>
    [ObservableProperty] private long _totalCalls;

    /// <summary>Number of brokers that have had at least one call recorded.</summary>
    [ObservableProperty] private int _trackedBrokerCount;

    /// <summary>True once any API activity has been recorded (drives the panel's empty state).</summary>
    [ObservableProperty] private bool _hasActivity;

    /// <summary>One-line summary shown in the dropdown footer ("3 brokers · 12,480 calls").</summary>
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

    public void Dispose()
    {
        _timer.Stop();
    }
}
