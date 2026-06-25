using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>
/// One chip in the header API-meter strip. Avalonia mirror of the WPF
/// <c>TradingTerminal.App.BrokerMetering.BrokerApiChipViewModel</c> — identical logic, so the
/// header reads the same on both heads. Bound by <see cref="BrokerApiMeterViewModel"/>.
/// </summary>
public sealed partial class BrokerApiChipViewModel : ViewModelBase
{
    public BrokerApiChipViewModel(BrokerKind broker)
    {
        Broker = broker;
        Label = LabelFor(broker);
    }

    public BrokerKind Broker { get; }

    public string Label { get; }

    [ObservableProperty] private long _totalCalls;
    [ObservableProperty] private int _callsLastMinute;
    [ObservableProperty] private int _softLimitPerMinute;

    public int AvailableCallsPerMinute => SoftLimitPerMinute > 0
        ? Math.Max(0, SoftLimitPerMinute - CallsLastMinute)
        : 0;

    public BrokerApiChipStatus Status => SoftLimitPerMinute <= 0
        ? BrokerApiChipStatus.Untracked
        : (CallsLastMinute * 100.0 / SoftLimitPerMinute) switch
        {
            < 50 => BrokerApiChipStatus.Healthy,
            < 80 => BrokerApiChipStatus.Warming,
            _ => BrokerApiChipStatus.Hot,
        };

    public string UsageDisplay => SoftLimitPerMinute > 0
        ? $"{CallsLastMinute}/{SoftLimitPerMinute}"
        : $"{CallsLastMinute}";

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

/// <summary>Drives the chip's background colour bucket.</summary>
public enum BrokerApiChipStatus
{
    Untracked,
    Healthy,
    Warming,
    Hot,
}
