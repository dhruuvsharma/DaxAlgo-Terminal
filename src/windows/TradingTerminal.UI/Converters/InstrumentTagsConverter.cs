using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Projects a <see cref="SignalInstrument"/> into the ordered list of coloured pills shown next to
/// it in the instrument dropdowns: the source broker (when known), the asset class, and the data
/// types that broker can serve for it. Pure presentation — the capability matrix mirrors the
/// <see cref="IBrokerClient"/> implementations (L2 → cTrader only; trade tape → IB only; bars + L1
/// everywhere). Returns an empty list for anything that isn't a <see cref="SignalInstrument"/>.
/// </summary>
public sealed class InstrumentTagsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tags = new List<InstrumentTag>();
        if (value is not SignalInstrument instrument) return tags;

        if (instrument.Broker is { } broker)
            tags.Add(BrokerTag(broker));

        tags.Add(CategoryTag(instrument));

        foreach (var data in DataTypes(instrument.Broker))
            tags.Add(new InstrumentTag(data, DataBg, DataFg));

        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // ── Broker pills (one vivid colour each) ───────────────────────────────────────────────
    private static InstrumentTag BrokerTag(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => new("IB",      Brush("#1565C0"), White),
        BrokerKind.NinjaTrader        => new("NT",      Brush("#2E7D32"), White),
        BrokerKind.CTrader            => new("cTrader", Brush("#6A1B9A"), White),
        BrokerKind.Alpaca             => new("Alpaca",  Brush("#F2A900"), Black),
        _                             => new(broker.ToString(), Brush("#607D8B"), White),
    };

    // ── Asset-class pill (derived from SecType, refined by the category label) ──────────────
    private static InstrumentTag CategoryTag(SignalInstrument i)
    {
        var sec = i.Contract.SecType?.ToUpperInvariant() ?? "";
        var cat = i.Category ?? "";
        var (text, hex) = sec switch
        {
            "CASH"                       => ("FX",     "#5C6BC0"),
            "CRYPTO"                     => ("CRYPTO", "#F7931A"),
            "CONTFUT" or "FUT" or "FUTURES" => ("FUT", "#8D6E63"),
            "IND" or "INDEX"             => ("INDEX",  "#7E57C2"),
            "OPT"                        => ("OPT",    "#C2185B"),
            "STK" or "STOCK" => cat.Contains("ETF", StringComparison.OrdinalIgnoreCase)
                ? ("ETF", "#00897B")
                : ("STOCK", "#546E7A"),
            _ => (ShortCategory(cat, sec), "#607D8B"),
        };
        var fg = text == "CRYPTO" ? Black : White;
        return new InstrumentTag(text, Brush(hex), fg);
    }

    private static string ShortCategory(string category, string secType)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            var first = category.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (first.Length > 0) return first[0].ToUpperInvariant();
        }
        return string.IsNullOrWhiteSpace(secType) ? "—" : secType;
    }

    // ── Data-capability pills (broker-level; bars + L1 universal) ───────────────────────────
    private static IEnumerable<string> DataTypes(BrokerKind? broker)
    {
        yield return "BAR";
        yield return "L1";
        if (broker == BrokerKind.CTrader) yield return "L2";
        if (broker == BrokerKind.InteractiveBrokers) yield return "TAPE";
    }

    // ── Brush helpers (frozen for reuse across many rows) ──────────────────────────────────
    private static readonly Brush White = Brush("#FFFFFF");
    private static readonly Brush Black = Brush("#1B1B1B");
    private static readonly Brush DataBg = Brush("#334155");   // muted slate — groups the data pills
    private static readonly Brush DataFg = Brush("#CFD8DC");

    private static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
