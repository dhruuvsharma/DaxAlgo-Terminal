using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI;

namespace TradingTerminal.App.BrokerMetering;

/// <summary>
/// One chip in the header API-meter strip. Bound by <see cref="BrokerApiMeterViewModel"/>'s
/// ObservableCollection; the DataTemplate in MainWindow.xaml renders it.
/// </summary>
public sealed partial class BrokerApiChipViewModel : ViewModelBase
{
    public BrokerApiChipViewModel(BrokerKind broker)
    {
        Broker = broker;
        Label = LabelFor(broker);
    }

    public BrokerKind Broker { get; }

    /// <summary>Short three-letter-ish label shown on the chip ("IB", "NT", "CT", "AL").</summary>
    public string Label { get; }

    [ObservableProperty]
    private long _totalCalls;

    [ObservableProperty]
    private int _callsLastMinute;

    /// <summary>0 when the broker has no known soft cap (e.g. NinjaTrader is local).</summary>
    [ObservableProperty]
    private int _softLimitPerMinute;

    /// <summary>Remaining headroom against the soft cap. 0 when SoftLimitPerMinute is 0.</summary>
    public int AvailableCallsPerMinute => SoftLimitPerMinute > 0
        ? Math.Max(0, SoftLimitPerMinute - CallsLastMinute)
        : 0;

    /// <summary>Drives the chip background — green / amber / red bucket on usage %.</summary>
    public BrokerApiChipStatus Status => SoftLimitPerMinute <= 0
        ? BrokerApiChipStatus.Untracked
        : (CallsLastMinute * 100.0 / SoftLimitPerMinute) switch
        {
            < 50 => BrokerApiChipStatus.Healthy,
            < 80 => BrokerApiChipStatus.Warming,
            _ => BrokerApiChipStatus.Hot,
        };

    /// <summary>"42/200" style summary used by the tooltip and chip body.</summary>
    public string UsageDisplay => SoftLimitPerMinute > 0
        ? $"{CallsLastMinute}/{SoftLimitPerMinute}"
        : $"{CallsLastMinute}";

    /// <summary>Tooltip text — gives the user the full breakdown without crowding the chip.</summary>
    public string TooltipText => SoftLimitPerMinute > 0
        ? $"{Broker}: {CallsLastMinute} calls in last minute ({AvailableCallsPerMinute} remaining vs soft cap of {SoftLimitPerMinute}/min). {TotalCalls:n0} total this session."
        : $"{Broker}: {CallsLastMinute} calls in last minute (no rate cap — local). {TotalCalls:n0} total this session.";

    partial void OnCallsLastMinuteChanged(int value)
    {
        OnPropertyChanged(nameof(AvailableCallsPerMinute));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(UsageDisplay));
        OnPropertyChanged(nameof(TooltipText));
    }

    partial void OnSoftLimitPerMinuteChanged(int value)
    {
        OnPropertyChanged(nameof(AvailableCallsPerMinute));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(UsageDisplay));
        OnPropertyChanged(nameof(TooltipText));
    }

    partial void OnTotalCallsChanged(long value) => OnPropertyChanged(nameof(TooltipText));

    private static string LabelFor(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NT",
        BrokerKind.CTrader => "CT",
        BrokerKind.Alpaca => "AL",
        _ => broker.ToString().Substring(0, Math.Min(2, broker.ToString().Length)).ToUpperInvariant(),
    };
}

/// <summary>Drives the chip's background colour. Mapped to a brush in XAML via a DataTrigger.</summary>
public enum BrokerApiChipStatus
{
    /// <summary>Broker has no known soft cap — chip stays neutral regardless of count.</summary>
    Untracked,
    Healthy,
    Warming,
    Hot,
}
