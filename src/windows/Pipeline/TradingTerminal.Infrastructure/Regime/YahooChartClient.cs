using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Pulls daily close series from Yahoo Finance's public v8 chart endpoint. Free, no key, but
/// unofficial — every failure (HTTP error, throttle, schema drift) folds into a null/empty
/// result so the regime composite degrades that input rather than blowing up.
/// </summary>
internal sealed class YahooChartClient
{
    public const string HttpClientName = "market-regime-yahoo";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<YahooChartClient> _logger;

    public YahooChartClient(IHttpClientFactory httpFactory, ILogger<YahooChartClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public sealed record Series(double? Price, double[] Closes)
    {
        public static Series Empty { get; } = new(null, Array.Empty<double>());
    }

    /// <summary>One year of daily closes plus the latest price for <paramref name="symbol"/>.</summary>
    public async Task<Series> GetAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range=1y";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Yahoo {Symbol}: HTTP {Status}", symbol, (int)resp.StatusCode);
                return Series.Empty;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var result = doc.RootElement.GetProperty("chart").GetProperty("result");
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
                return Series.Empty;
            var r0 = result[0];

            double? price = null;
            if (r0.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("regularMarketPrice", out var p) &&
                p.ValueKind == JsonValueKind.Number)
                price = p.GetDouble();

            var closes = new List<double>();
            if (r0.TryGetProperty("indicators", out var ind) &&
                ind.TryGetProperty("quote", out var quote) &&
                quote.ValueKind == JsonValueKind.Array && quote.GetArrayLength() > 0 &&
                quote[0].TryGetProperty("close", out var closeArr) &&
                closeArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in closeArr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Number)
                        closes.Add(el.GetDouble());
            }

            price ??= closes.Count > 0 ? closes[^1] : null;
            return new Series(price, closes.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Yahoo {Symbol} fetch failed", symbol);
            return Series.Empty;
        }
    }
}
