using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Projects an <see cref="ITradingStrategy"/> into the ordered list of coloured classification pills
/// shown beneath it in the Strategies pane — complementing the data-type pills from
/// <see cref="StrategyDataRequirementConverter"/>. Emitted in order:
/// <list type="number">
///   <item>asset-class pills (one per <see cref="ITradingStrategy.AssetClasses"/>, or a single
///         "ANY ASSET" pill when the strategy is asset-agnostic);</item>
///   <item>the scope pill — SINGLE-ASSET or MULTI-ASSET (<see cref="ITradingStrategy.AssetScope"/>);</item>
///   <item>the broker-capability pill — ANY BROKER / NEEDS TAPE / NEEDS L2, derived from the
///         strategy's <see cref="ITradingStrategy.DataRequirement"/>;</item>
///   <item>one broker chip per <see cref="ITradingStrategy.SupportedBrokers"/> (omitted entirely
///         for broker-agnostic strategies, whose ANY BROKER pill already says so).</item>
/// </list>
/// Colours mirror <see cref="InstrumentTagsConverter"/> (broker + asset-class palettes) so a
/// strategy's pills read the same as the instrument pills. Any non-<see cref="ITradingStrategy"/>
/// value returns an empty list. Register via <see cref="EnsureConverterRegistered"/> (mirrors
/// <see cref="StrategyDataRequirementConverter"/>).
/// </summary>
public sealed class StrategyClassificationConverter : IValueConverter
{
    /// <summary>
    /// Resource key under which the shared instance is registered in <see cref="Application"/>
    /// resources. XAML usage: <c>Converter="{StaticResource StrategyClassConverter}"</c>.
    /// </summary>
    public const string ConverterKey = "StrategyClassConverter";

    // ── Scope pills ──────────────────────────────────────────────────────────────────────────
    private static readonly InstrumentTag PillSingle = new("SINGLE-ASSET", Brush("#37474F"), Brush("#CFD8DC"));
    private static readonly InstrumentTag PillMulti  = new("MULTI-ASSET",  Brush("#00838F"), White);

    // ── Broker-capability pills (derived from the data appetite) ─────────────────────────────
    private static readonly InstrumentTag PillAnyBroker = new("ANY BROKER", Brush("#2E7D32"), White);
    private static readonly InstrumentTag PillNeedsTape = new("NEEDS TAPE", Brush("#A65A00"), Brush("#FFE0A0"));
    private static readonly InstrumentTag PillNeedsL2   = new("NEEDS L2",   Brush("#A65A00"), Brush("#FFE0A0"));

    // ── Asset-agnostic pill ──────────────────────────────────────────────────────────────────
    private static readonly InstrumentTag PillAnyAsset = new("ANY ASSET", Brush("#455A64"), White);

    // ── IValueConverter ───────────────────────────────────────────────────────────────────────

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tags = new List<InstrumentTag>(8);
        if (value is not ITradingStrategy strategy) return tags;

        // 1. Asset classes (or ANY ASSET when agnostic).
        if (strategy.AssetClasses.Count == 0)
            tags.Add(PillAnyAsset);
        else
            foreach (var ac in strategy.AssetClasses)
                tags.Add(AssetClassTag(ac));

        // 2. Single vs multi.
        tags.Add(strategy.AssetScope == StrategyAssetScope.MultiAsset ? PillMulti : PillSingle);

        // 3. Broker-capability summary pill.
        var req = strategy.DataRequirement;
        if (req.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add(PillNeedsTape);
        else if (req.HasFlag(StrategyDataRequirement.Depth)) tags.Add(PillNeedsL2);
        else tags.Add(PillAnyBroker);

        // 4. Explicit broker chips (only when the strategy is broker-restricted).
        foreach (var broker in strategy.SupportedBrokers)
            tags.Add(BrokerTag(broker));

        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // ── Asset-class palette (matches InstrumentTagsConverter.CategoryTag) ─────────────────────
    private static InstrumentTag AssetClassTag(AssetClass ac) => ac switch
    {
        AssetClass.Equity => new("STOCK",  Brush("#546E7A"), White),
        AssetClass.Future => new("FUT",    Brush("#8D6E63"), White),
        AssetClass.Forex  => new("FX",     Brush("#5C6BC0"), White),
        AssetClass.Crypto => new("CRYPTO", Brush("#F7931A"), Black),
        AssetClass.Option => new("OPT",    Brush("#C2185B"), White),
        AssetClass.Index  => new("INDEX",  Brush("#7E57C2"), White),
        _                 => new("ASSET",  Brush("#607D8B"), White),
    };

    // ── Broker palette (matches InstrumentTagsConverter.BrokerTag, extended to all backends) ──
    private static InstrumentTag BrokerTag(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => new("IB",       Brush("#1565C0"), White),
        BrokerKind.NinjaTrader        => new("NT",       Brush("#2E7D32"), White),
        BrokerKind.CTrader            => new("cTrader",  Brush("#6A1B9A"), White),
        BrokerKind.Alpaca             => new("Alpaca",   Brush("#F2A900"), Black),
        BrokerKind.Simulated          => new("SIM",      Brush("#607D8B"), White),
        BrokerKind.Binance            => new("Binance",  Brush("#F0B90B"), Black),
        BrokerKind.IronBeam           => new("Ironbeam", Brush("#455A64"), White),
        BrokerKind.LondonStrategicEdge=> new("LSE",      Brush("#00695C"), White),
        BrokerKind.Upstox             => new("Upstox",   Brush("#5A2D82"), White),
        BrokerKind.Coinbase           => new("Coinbase", Brush("#1652F0"), White),
        BrokerKind.Bybit              => new("Bybit",    Brush("#F7A600"), Black),
        BrokerKind.Kraken             => new("Kraken",   Brush("#5741D9"), White),
        BrokerKind.Okx                => new("OKX",      Brush("#1B1B1B"), White),
        _                             => new(broker.ToString(), Brush("#607D8B"), White),
    };

    // ── App-resource registration (MC3074 workaround — mirrors StrategyDataRequirementConverter) ─

    /// <summary>
    /// Registers a single shared <see cref="StrategyClassificationConverter"/> in
    /// <see cref="Application"/> resources under <see cref="ConverterKey"/>. Idempotent; no-op at
    /// design-time / headless hosts.
    /// </summary>
    public static void EnsureConverterRegistered()
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Resources.Contains(ConverterKey))
            app.Resources[ConverterKey] = new StrategyClassificationConverter();
    }

    // ── Brush helpers (frozen for reuse across many rows) ────────────────────────────────────
    private static readonly Brush White = Brush("#FFFFFF");
    private static readonly Brush Black = Brush("#1B1B1B");

    private static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
