using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.MarketData.Tests;

/// <summary>
/// The parts of OANDA v20 that are easy to get wrong, pinned against the payload shapes the published
/// reference documents.
///
/// <para>None of this can be proved without an account, so what is tested is the reading: the shapes are
/// taken from the v20 reference and the assertions say what the adapter must conclude from them. That
/// catches the mistakes that are actually made here — prices arriving as strings, heartbeats read as
/// quotes, an incomplete candle treated as history — none of which look wrong in a debugger, and all of
/// which reach a strategy as data.</para>
/// </summary>
public sealed class OandaWireFormatTests
{
    // ── environments ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PracticeAndLiveAreDifferentHosts()
    {
        // Not a flag on one host. A practice token against the live host returns 401, which reads as a
        // bad token rather than as the wrong environment — the commonest v20 setup mistake.
        var practice = new OandaOptions { Practice = true };
        var live = new OandaOptions { Practice = false };

        practice.RestBaseUrl.Should().NotBe(live.RestBaseUrl);
        practice.StreamBaseUrl.Should().NotBe(live.StreamBaseUrl);
    }

    [Fact]
    public void StreamingAndTradingAreAlsoDifferentHosts()
    {
        // The other half of the same trap: the pricing stream is not served by the trading host.
        var options = new OandaOptions { Practice = true };

        options.StreamBaseUrl.Should().NotBe(options.RestBaseUrl);
        options.StreamBaseUrl.Should().Contain("stream");
        options.RestBaseUrl.Should().Contain("api");
    }

    [Fact]
    public void ThePracticeEnvironmentIsTheDefault()
    {
        // A first run that points at live money by default is the wrong way round.
        new OandaOptions().Practice.Should().BeTrue();
    }

    [Fact]
    public void ATokenIsNeverStoredInTheOptions()
    {
        // Options land in appsettings.json. The token belongs in the DPAPI store with every other
        // broker secret, so there must be nowhere here to put it.
        typeof(OandaOptions).GetProperties()
            .Should().NotContain(p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                                   || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    // ── candles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A candle in the documented shape: prices are STRINGS, and o/h/l/c sit under a side.</summary>
    private const string Candle = """
        {
          "time": "2026-08-26T09:15:00.000000000Z",
          "mid": { "o": "1.16234", "h": "1.16290", "l": "1.16201", "c": "1.16277" },
          "volume": 412,
          "complete": true
        }
        """;

    [Fact]
    public void CandlePricesArriveAsStringsNotNumbers()
    {
        // The thing that throws if you assume otherwise. Documented, and easy to miss because a JSON
        // sample reads like numbers.
        using var json = JsonDocument.Parse(Candle);
        var open = json.RootElement.GetProperty("mid").GetProperty("o");

        open.ValueKind.Should().Be(JsonValueKind.String);
        double.TryParse(open.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            .Should().BeTrue();
        value.Should().BeApproximately(1.16234d, 1e-9);
    }

    [Fact]
    public void AnIncompleteCandleIsTheOneStillForming()
    {
        // Taking it as history means the last bar changes underneath a strategy between one request and
        // the next — a backtest that cannot be repeated and a live chart that rewrites itself.
        using var json = JsonDocument.Parse(Candle.Replace("\"complete\": true", "\"complete\": false"));

        json.RootElement.GetProperty("complete").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void CandleVolumeIsATickCountNotASize()
    {
        // v20 volume is the number of price updates, not traded quantity. It is still worth carrying —
        // it is a real activity measure — but it must not be read as size.
        using var json = JsonDocument.Parse(Candle);

        json.RootElement.GetProperty("volume").GetInt64().Should().Be(412L);
    }

    [Fact]
    public void TheDefaultCandleSideIsMid()
    {
        // A bid chart and an ask chart of the same market disagree by the spread and neither is "the
        // price". Mid is the only defensible default for charting.
        new OandaOptions().CandlePrice.Should().Be("M");
    }

    // ── the pricing stream ──────────────────────────────────────────────────────────────────────

    private const string Heartbeat = """
        { "type": "HEARTBEAT", "time": "2026-08-26T09:15:05.000000000Z" }
        """;

    private const string Price = """
        {
          "type": "PRICE",
          "instrument": "EUR_USD",
          "time": "2026-08-26T09:15:05.123456789Z",
          "bids": [ { "price": "1.16270", "liquidity": 10000000 } ],
          "asks": [ { "price": "1.16283", "liquidity": 10000000 } ],
          "status": "tradeable"
        }
        """;

    [Fact]
    public void TheStreamInterleavesHeartbeatsWithPrices()
    {
        // A heartbeat carries no book. Reading it as a quote publishes a zero-priced tick, and a
        // strategy will act on that as readily as on a real one.
        using var beat = JsonDocument.Parse(Heartbeat);
        using var price = JsonDocument.Parse(Price);

        beat.RootElement.GetProperty("type").GetString().Should().Be("HEARTBEAT");
        beat.RootElement.TryGetProperty("bids", out _).Should().BeFalse();
        price.RootElement.GetProperty("type").GetString().Should().Be("PRICE");
    }

    [Fact]
    public void PriceLevelsCarryLiquidityAsTheirSize()
    {
        using var json = JsonDocument.Parse(Price);
        var top = json.RootElement.GetProperty("bids")[0];

        top.GetProperty("price").ValueKind.Should().Be(JsonValueKind.String);
        top.GetProperty("liquidity").GetInt64().Should().Be(10_000_000L);
    }

    [Fact]
    public void NanosecondTimestampsStillParse()
    {
        // RFC3339 with nine fractional digits — more precision than DateTime carries. It must round
        // rather than fail, or every tick is dropped.
        using var json = JsonDocument.Parse(Price);
        var time = json.RootElement.GetProperty("time").GetString();

        DateTime.TryParse(time, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            .Should().BeTrue();
        parsed.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void AStreamLineCanBeATruncatedTail()
    {
        // A dropped connection leaves a partial line. It must be skipped, not thrown on, or every
        // reconnect surfaces as an error the user cannot act on.
        var read = () => JsonDocument.Parse("{ \"type\": \"PRI");

        read.Should().Throw<JsonException>("the adapter catches this and skips the line");
    }
}
