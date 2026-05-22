using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Regime;

/// <summary>
/// Scrapes the weekly AAII bull/bear sentiment survey. Lowest-reliability source: it parses
/// HTML and breaks if the page layout shifts, so it is always optional and non-blocking. The
/// first three <c>class="tableTxt"</c> percentage cells of the results table are, positionally,
/// Bullish / Neutral / Bearish (the upstream project relies on the same ordering).
/// </summary>
internal sealed partial class AaiiSentimentClient
{
    public const string HttpClientName = "market-regime-aaii";
    private const string Url = "https://www.aaii.com/sentimentsurvey/sent_results";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AaiiSentimentClient> _logger;

    public AaiiSentimentClient(IHttpClientFactory httpFactory, ILogger<AaiiSentimentClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public sealed record Sentiment(double Bull, double Bear);

    public async Task<Sentiment?> GetAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(Url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var matches = TableTxtPercent().Matches(html);
            if (matches.Count < 3) return null;

            var pcts = new double[3];
            for (var k = 0; k < 3; k++)
                if (!double.TryParse(matches[k].Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out pcts[k]))
                    return null;

            return new Sentiment(Bull: pcts[0], Bear: pcts[2]);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AAII fetch failed (sentiment will degrade)");
            return null;
        }
    }

    [GeneratedRegex("<td[^>]*class=\"tableTxt\"[^>]*>([\\d.]+)%", RegexOptions.IgnoreCase)]
    private static partial Regex TableTxtPercent();
}
