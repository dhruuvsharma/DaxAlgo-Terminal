using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Reads CNN's Fear &amp; Greed index from its dataviz endpoint. Free, no key. The endpoint
/// bot-blocks a Windows UA (returns 418), so the named HttpClient is configured with a Mac UA
/// and the markets Referer (see <see cref="RegimeServiceCollectionExtensions"/>).
/// </summary>
internal sealed class CnnFearGreedClient
{
    public const string HttpClientName = "market-regime-cnn";
    private const string Url = "https://production.dataviz.cnn.io/index/fearandgreed/current";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CnnFearGreedClient> _logger;

    public CnnFearGreedClient(IHttpClientFactory httpFactory, ILogger<CnnFearGreedClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>The current 0–100 index, or null if unavailable.</summary>
    public async Task<int?> GetAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(Url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("CNN F&G: HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = doc.RootElement;
            // Shape is either { score, rating } or { fear_and_greed: { score, rating } }.
            if (TryScore(root, out var s)) return s;
            if (root.TryGetProperty("fear_and_greed", out var inner) && TryScore(inner, out var s2)) return s2;
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CNN F&G fetch failed");
            return null;
        }
    }

    private static bool TryScore(JsonElement el, out int score)
    {
        score = 0;
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number)
        {
            score = (int)Math.Round(s.GetDouble());
            return true;
        }
        return false;
    }
}
