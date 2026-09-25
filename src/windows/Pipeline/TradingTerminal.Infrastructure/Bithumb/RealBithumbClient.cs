using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Upbit;

namespace TradingTerminal.Infrastructure.Bithumb;

/// <summary>
/// Bithumb — Korean won markets — over its public socket (no key, no account). Its current API copies
/// Upbit's field for field, so everything but the bars is the shared
/// <see cref="UpbitShapedClient{TOptions}"/>.
///
/// <para><b>No candle stream.</b> Subscribing to <c>candle.1m</c> is answered with
/// <c>INVALID_PARAM</c> and a closed socket, so live intraday bars are built from the trade tape and the
/// daily bar from the ticker. History comes from REST candles as at Upbit.</para>
///
/// <para>Bithumb's trading day starts at midnight Korea time (15:00 UTC), not UTC midnight.</para>
/// </summary>
internal sealed class RealBithumbClient : UpbitShapedClient<BithumbOptions>
{
    public RealBithumbClient(ILogger<RealBithumbClient> logger, IOptions<BithumbOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Bithumb;
    protected override string VenueName => "Bithumb";
    protected override string ExchangeCode => "BITHUMB";
    protected override TimeSpan DayStartOffset => TimeSpan.FromHours(-9);

    protected override bool HasLiveInterval(BarSize size) => false;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        throw new NotSupportedException("Bithumb publishes no candle stream.");

    protected override IAsyncEnumerable<Bar> SynthesiseBarsAsync(Contract contract, BarSize size, CancellationToken ct) =>
        size == BarSize.OneDay ? DailyBarsFromTickerAsync(contract, ct) : BarsFromTradesAsync(contract, size, ct);
}
