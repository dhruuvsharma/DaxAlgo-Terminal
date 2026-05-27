using System.Collections.ObjectModel;
using System.Windows.Threading;
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
public sealed class BrokerApiMeterViewModel : ViewModelBase, IDisposable
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
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
