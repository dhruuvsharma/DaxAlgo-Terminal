namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// Five bullish/bearish bands the signed composite falls into. Boundaries are symmetric around
/// 0 — the per-instrument analyzer doesn't have a "neutral with bias" notion, so the bands
/// straddle zero.
/// </summary>
public enum InstrumentRegimeBand
{
    StrongSell,
    Sell,
    Neutral,
    Buy,
    StrongBuy,
}

public static class InstrumentRegimeBandExtensions
{
    /// <summary>Maps a signed composite score in <c>[-100, +100]</c> to its band.</summary>
    public static InstrumentRegimeBand FromScore(double score) => score switch
    {
        <= -60 => InstrumentRegimeBand.StrongSell,
        <= -20 => InstrumentRegimeBand.Sell,
        <   20 => InstrumentRegimeBand.Neutral,
        <   60 => InstrumentRegimeBand.Buy,
        _      => InstrumentRegimeBand.StrongBuy,
    };

    public static string Label(this InstrumentRegimeBand band) => band switch
    {
        InstrumentRegimeBand.StrongSell => "Strong Sell",
        InstrumentRegimeBand.Sell       => "Sell",
        InstrumentRegimeBand.Neutral    => "Neutral",
        InstrumentRegimeBand.Buy        => "Buy",
        InstrumentRegimeBand.StrongBuy  => "Strong Buy",
        _                                => "Unknown",
    };
}
