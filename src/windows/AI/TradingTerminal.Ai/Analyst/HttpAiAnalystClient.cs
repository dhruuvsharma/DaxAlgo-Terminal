using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Infrastructure.Notifications;

namespace TradingTerminal.Infrastructure.AiAnalyst;

/// <summary>
/// HTTP client for the Python <c>daxalgo-ml</c> sidecar's <c>/analyst/run</c> endpoint.
/// Times out cleanly per <see cref="AiAnalystOptions.TimeoutSeconds"/> and folds every
/// failure mode into <see cref="AnalystReport.Unavailable"/> — the contract is "never
/// throw", same shape as the notification enricher pipeline.
/// </summary>
internal sealed class HttpAiAnalystClient : IAiAnalystClient
{
    public const string HttpClientName = "ai-analyst";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<HttpAiAnalystClient> _logger;

    public HttpAiAnalystClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<HttpAiAnalystClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable => _options.CurrentValue.AiAnalyst.Enabled;

    public async Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default)
    {
        var o = _options.CurrentValue.AiAnalyst;
        if (!o.Enabled) return AnalystReport.Unavailable("AI Analyst disabled in settings.");
        if (string.IsNullOrWhiteSpace(o.Endpoint))
            return AnalystReport.Unavailable("AI Analyst endpoint is empty.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));

            var http = _httpFactory.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(o.Endpoint.TrimEnd('/') + "/");

            var payload = new HttpRequest(
                request.Symbol,
                request.Timeframe,
                request.BarCount,
                request.Provider,
                request.Model,
                request.VisionModel,
                o.ApiKey ?? string.Empty,
                request.Bars);

            using var response = await http.PostAsJsonAsync("analyst/run", payload, JsonOptions, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("AI Analyst HTTP {Status} — returning unavailable", (int)response.StatusCode);
                return AnalystReport.Unavailable($"Python sidecar returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content
                .ReadFromJsonAsync<HttpAnalystReport>(JsonOptions, cts.Token)
                .ConfigureAwait(false);
            if (body is null) return AnalystReport.Unavailable("Python sidecar returned empty body.");

            return body.ToReport();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("AI Analyst run timed out after {Sec}s", o.TimeoutSeconds);
            return AnalystReport.Unavailable($"AI Analyst timed out after {o.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "AI Analyst sidecar unreachable");
            return AnalystReport.Unavailable(
                "AI sidecar unreachable — start it from the login screen's Services panel, or enable "
                + "auto-start in Settings → Research (the app then launches daxalgo-ml itself), and retry.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI Analyst run failed");
            return AnalystReport.Unavailable($"AI Analyst run failed: {ex.Message}");
        }
    }

    private sealed record HttpRequest(
        string Symbol,
        string Timeframe,
        int BarCount,
        string Provider,
        string Model,
        string VisionModel,
        string ApiKey,
        IReadOnlyList<AnalystBar> Bars);

    private sealed record HttpAnalystReport(
        string Decision,
        string ForecastHorizon,
        double RiskRewardRatio,
        double Confidence,
        string Justification,
        HttpIndicatorReport Indicator,
        HttpPatternReport Pattern,
        HttpTrendReport Trend,
        string PatternChartPngBase64,
        string TrendChartPngBase64,
        long ElapsedMs)
    {
        public AnalystReport ToReport() => new(
            Decision: ParseDecision(Decision),
            ForecastHorizon: ForecastHorizon ?? "—",
            RiskRewardRatio: RiskRewardRatio,
            Confidence: Confidence,
            Justification: Justification ?? string.Empty,
            Indicator: new IndicatorReport(Indicator?.Summary ?? string.Empty,
                Indicator?.Values ?? new Dictionary<string, double>()),
            Pattern: new PatternReport(Pattern?.PatternName ?? "None",
                Pattern?.Confidence ?? 0, Pattern?.Reasoning ?? string.Empty),
            Trend: new TrendReport(Trend?.Direction ?? "Flat",
                Trend?.Slope ?? 0, Trend?.ChannelUpper ?? 0, Trend?.ChannelLower ?? 0,
                Trend?.Reasoning ?? string.Empty),
            PatternChartPngBase64: PatternChartPngBase64 ?? string.Empty,
            TrendChartPngBase64: TrendChartPngBase64 ?? string.Empty,
            ElapsedMs: ElapsedMs);

        private static AiAnalystDecision ParseDecision(string? raw) => raw?.ToLowerInvariant() switch
        {
            "long" or "buy" => AiAnalystDecision.Long,
            "short" or "sell" => AiAnalystDecision.Short,
            _ => AiAnalystDecision.NoCall,
        };
    }

    private sealed record HttpIndicatorReport(string Summary, Dictionary<string, double> Values);
    private sealed record HttpPatternReport(string PatternName, double Confidence, string Reasoning);
    private sealed record HttpTrendReport(string Direction, double Slope, double ChannelUpper, double ChannelLower, string Reasoning);
}
