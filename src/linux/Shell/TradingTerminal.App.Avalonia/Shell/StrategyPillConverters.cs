using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>One coloured catalog pill: label + background/foreground brushes. Avalonia mirror of the
/// WPF <c>InstrumentTag</c> the strategy-pill converters emit.</summary>
public sealed record StrategyPill(string Text, IBrush Background, IBrush Foreground);

/// <summary>
/// Avalonia port of <c>StrategyDataRequirementConverter</c> (WPF). Projects an
/// <see cref="ITradingStrategy"/>'s <see cref="StrategyDataRequirement"/> into L1 → BAR → L2 → TAPE
/// pills, using the same hex palette as the WPF converter so the catalog cards read identically.
/// </summary>
public sealed class StrategyDataRequirementConverter : IValueConverter
{
    private static readonly IBrush BaselineBg = Brush("#334155"); // muted slate
    private static readonly IBrush BaselineFg = Brush("#CFD8DC");
    private static readonly IBrush ExtraBg = Brush("#A65A00");     // Accent.Dim
    private static readonly IBrush ExtraFg = Brush("#FFE0A0");

    private static readonly StrategyPill PillL1 = new("L1", BaselineBg, BaselineFg);
    private static readonly StrategyPill PillBar = new("BAR", BaselineBg, BaselineFg);
    private static readonly StrategyPill PillL2 = new("L2", ExtraBg, ExtraFg);
    private static readonly StrategyPill PillTape = new("TAPE", ExtraBg, ExtraFg);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var req = value switch
        {
            StrategyDataRequirement r => r,
            ITradingStrategy s => s.DataRequirement,
            _ => (StrategyDataRequirement?)null,
        };

        var tags = new List<StrategyPill>(4);
        if (req is null) return tags;

        if (req.Value.HasFlag(StrategyDataRequirement.L1)) tags.Add(PillL1);
        if (req.Value.HasFlag(StrategyDataRequirement.Bars)) tags.Add(PillBar);
        if (req.Value.HasFlag(StrategyDataRequirement.Depth)) tags.Add(PillL2);
        if (req.Value.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add(PillTape);
        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// Avalonia port of <c>StrategyClassificationConverter</c> (WPF). Emits, in order: asset-class pills
/// (or ANY ASSET), the SINGLE/MULTI-ASSET scope pill, the broker-capability pill (ANY BROKER / NEEDS
/// TAPE / NEEDS L2), then one chip per supported broker. Palette is 1:1 with the WPF converter.
/// </summary>
public sealed class StrategyClassificationConverter : IValueConverter
{
    private static readonly IBrush White = Brush("#FFFFFF");
    private static readonly IBrush Black = Brush("#1B1B1B");

    private static readonly StrategyPill PillSingle = new("SINGLE-ASSET", Brush("#37474F"), Brush("#CFD8DC"));
    private static readonly StrategyPill PillMulti = new("MULTI-ASSET", Brush("#00838F"), White);
    private static readonly StrategyPill PillAnyBroker = new("ANY BROKER", Brush("#2E7D32"), White);
    private static readonly StrategyPill PillNeedsTape = new("NEEDS TAPE", Brush("#A65A00"), Brush("#FFE0A0"));
    private static readonly StrategyPill PillNeedsL2 = new("NEEDS L2", Brush("#A65A00"), Brush("#FFE0A0"));
    private static readonly StrategyPill PillAnyAsset = new("ANY ASSET", Brush("#455A64"), White);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tags = new List<StrategyPill>(8);
        if (value is not ITradingStrategy strategy) return tags;

        if (strategy.AssetClasses.Count == 0)
            tags.Add(PillAnyAsset);
        else
            foreach (var ac in strategy.AssetClasses)
                tags.Add(AssetClassTag(ac));

        tags.Add(strategy.AssetScope == StrategyAssetScope.MultiAsset ? PillMulti : PillSingle);

        var req = strategy.DataRequirement;
        if (req.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add(PillNeedsTape);
        else if (req.HasFlag(StrategyDataRequirement.Depth)) tags.Add(PillNeedsL2);
        else tags.Add(PillAnyBroker);

        foreach (var broker in strategy.SupportedBrokers)
            tags.Add(BrokerTag(broker));

        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static StrategyPill AssetClassTag(AssetClass ac) => ac switch
    {
        AssetClass.Equity => new("STOCK", Brush("#546E7A"), White),
        AssetClass.Future => new("FUT", Brush("#8D6E63"), White),
        AssetClass.Forex => new("FX", Brush("#5C6BC0"), White),
        AssetClass.Crypto => new("CRYPTO", Brush("#F7931A"), Black),
        AssetClass.Option => new("OPT", Brush("#C2185B"), White),
        AssetClass.Index => new("INDEX", Brush("#7E57C2"), White),
        _ => new("ASSET", Brush("#607D8B"), White),
    };

    private static StrategyPill BrokerTag(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => new("IB", Brush("#1565C0"), White),
        BrokerKind.NinjaTrader => new("NT", Brush("#2E7D32"), White),
        BrokerKind.CTrader => new("cTrader", Brush("#6A1B9A"), White),
        BrokerKind.Alpaca => new("Alpaca", Brush("#F2A900"), Black),
        BrokerKind.Simulated => new("SIM", Brush("#607D8B"), White),
        BrokerKind.Binance => new("Binance", Brush("#F0B90B"), Black),
        BrokerKind.IronBeam => new("Ironbeam", Brush("#455A64"), White),
        BrokerKind.LondonStrategicEdge => new("LSE", Brush("#00695C"), White),
        BrokerKind.Upstox => new("Upstox", Brush("#5A2D82"), White),
        BrokerKind.Coinbase => new("Coinbase", Brush("#1652F0"), White),
        BrokerKind.Bybit => new("Bybit", Brush("#F7A600"), Black),
        BrokerKind.Kraken => new("Kraken", Brush("#5741D9"), White),
        BrokerKind.Okx => new("OKX", Brush("#1B1B1B"), White),
        _ => new(broker.ToString(), Brush("#607D8B"), White),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>True when the bound string is non-empty — drives the research-paper pill's visibility
/// (Avalonia uses <c>IsVisible</c> rather than WPF's Visibility).</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
