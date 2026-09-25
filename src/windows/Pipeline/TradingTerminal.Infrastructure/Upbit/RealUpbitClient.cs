using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Upbit;

/// <summary>
/// Upbit — Korea's largest exchange, won-denominated — over its public socket (no key, no account).
/// Everything but the candles is the shared <see cref="UpbitShapedClient{TOptions}"/>. Upbit streams
/// intraday candles (<c>candle.1m</c> … <c>candle.60m</c>); the daily bar is built from the ticker's
/// running day values, since there is no daily candle stream.
/// </summary>
internal sealed class RealUpbitClient : UpbitShapedClient<UpbitOptions>
{
    public RealUpbitClient(ILogger<RealUpbitClient> logger, IOptions<UpbitOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Upbit;
    protected override string VenueName => "Upbit";
    protected override string ExchangeCode => "UPBIT";
    protected override TimeSpan DayStartOffset => TimeSpan.Zero;

    protected override bool HasLiveInterval(BarSize size) => size != BarSize.OneDay;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub("candle." + Interval(size), symbol), "candle", el => ParseCandle(el, Options.SizeScale), ct);

    protected override IAsyncEnumerable<Bar> SynthesiseBarsAsync(Contract contract, BarSize size, CancellationToken ct) =>
        DailyBarsFromTickerAsync(contract, ct);

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "60m",
        _ => "1m",
    };
}
