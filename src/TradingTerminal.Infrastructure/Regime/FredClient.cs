using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Reads observation series from the FRED REST API (https://fred.stlouisfed.org/docs/api/).
/// Requires a free API key; without one the caller skips it and the credit/liquidity/macro
/// categories degrade to neutral. Returns values oldest→newest, dropping FRED's "." missing
/// markers, capped to the most recent ~420 observations (enough for daily 200-day windows and
/// weekly 52-week year-over-year comparisons).
/// </summary>
internal sealed class FredClient
{
    public const string HttpClientName = "market-regime-fred";
    private const int MaxObservations = 420;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<FredClient> _logger;

    public FredClient(IHttpClientFactory httpFactory, ILogger<FredClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<double[]> GetSeriesAsync(string seriesId, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return Array.Empty<double>();
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var url = $"https://api.stlouisfed.org/fred/series/observations?series_id={Uri.EscapeDataString(seriesId)}"
                      + $"&api_key={Uri.EscapeDataString(apiKey)}&file_type=json&sort_order=desc&limit={MaxObservations}";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("FRED {Series}: HTTP {Status}", seriesId, (int)resp.StatusCode);
                return Array.Empty<double>();
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("observations", out var obs) ||
                obs.ValueKind != JsonValueKind.Array)
                return Array.Empty<double>();

            // Response is newest→oldest (sort_order=desc); collect then reverse to chronological.
            var values = new List<double>(MaxObservations);
            foreach (var o in obs.EnumerateArray())
            {
                if (o.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String &&
                    double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    values.Add(d);
            }
            values.Reverse();
            return values.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FRED {Series} fetch failed", seriesId);
            return Array.Empty<double>();
        }
    }
}
