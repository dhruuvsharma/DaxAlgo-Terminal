using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications.Ollama;

/// <summary>
/// Appends a local-LLM commentary line to every signal notification by calling a local
/// Ollama HTTP endpoint (default http://localhost:11434/api/generate). Free, runs entirely
/// on the user's machine, no API key, no data leaves the box.
///
/// Workflow tool, NOT a predictive signal — typical generation latency is 1–4 seconds, so
/// this only makes sense for low-frequency signal mode, not for sub-second HFT. The
/// dispatcher already has a queue ahead of this enricher; if Ollama is slow or
/// unreachable, the timeout kicks in and the original message goes through unchanged.
/// </summary>
internal sealed class OllamaCommentaryEnricher : INotificationEnricher
{
    public const string HttpClientName = "ollama";
    private const string DefaultSystemPrompt =
        "You are a concise trading-signal annotator. Given a strategy signal, reply in ONE short sentence " +
        "(≤ 20 words) noting one plausible reason or caveat. No disclaimers. No financial advice phrasing.";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<OllamaCommentaryEnricher> _logger;

    public OllamaCommentaryEnricher(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<OllamaCommentaryEnricher> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public bool ShouldRun(StrategyNotification notification)
    {
        var o = _options.CurrentValue.Ollama;
        // Only enrich actionable signals — skip idle / test / arm-stop noise.
        return o.Enabled
            && !string.IsNullOrWhiteSpace(o.Endpoint)
            && !string.IsNullOrWhiteSpace(o.Model)
            && notification.Kind is NotificationKind.Signal or NotificationKind.Trade;
    }

    public async Task<StrategyNotification> EnrichAsync(StrategyNotification notification, CancellationToken ct)
    {
        var o = _options.CurrentValue.Ollama;
        var systemPrompt = string.IsNullOrWhiteSpace(o.SystemPrompt) ? DefaultSystemPrompt : o.SystemPrompt;
        var userPrompt =
            $"Strategy: {notification.StrategyName}\n" +
            $"Symbol: {notification.Symbol}\n" +
            $"Direction: {notification.Direction ?? "(none)"}\n" +
            $"Detail: {notification.Message}\n" +
            $"Time: {notification.TimestampUtc:O}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));

            var http = _httpFactory.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(o.Endpoint.TrimEnd('/') + "/");

            using var response = await http.PostAsJsonAsync(
                "api/generate",
                new
                {
                    model = o.Model,
                    prompt = userPrompt,
                    system = systemPrompt,
                    stream = false,
                },
                cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Ollama returned {Status} — passing through unchanged", (int)response.StatusCode);
                return notification;
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token).ConfigureAwait(false);
            if (!body.TryGetProperty("response", out var resp)) return notification;
            var commentary = resp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(commentary)) return notification;

            return notification with
            {
                Message = $"{notification.Message}\n💭 {commentary}",
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ollama enrichment timed out — passing through unchanged");
            return notification;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama enrichment failed — passing through unchanged");
            return notification;
        }
    }
}
