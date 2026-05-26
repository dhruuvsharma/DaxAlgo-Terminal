using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications.AiAnalyst;

/// <summary>
/// Notification enricher that appends a one-line AI Analyst verdict to every signal /
/// trade notification before the dispatcher fans it out. Bars come from
/// <see cref="IMarketDataRepository"/> — strategies stay broker-agnostic, and the
/// abstraction wins again on the enricher path.
///
/// Contract: NEVER throw. Failure modes (sidecar down, timeout, analyst NoCall) all
/// return the original notification unchanged — Ollama and AI Analyst are independent
/// and both pass-through on error.
/// </summary>
internal sealed class AiAnalystEnricher : INotificationEnricher
{
    private readonly IAiAnalystClient _analyst;
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<AiAnalystEnricher> _logger;

    public AiAnalystEnricher(
        IAiAnalystClient analyst,
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<AiAnalystEnricher> logger)
    {
        _analyst = analyst;
        _repository = repository;
        _selector = selector;
        _options = options;
        _logger = logger;
    }

    public bool ShouldRun(StrategyNotification notification)
    {
        var o = _options.CurrentValue.AiAnalyst;
        return o.Enabled
            && o.IncludeInEnricher
            && _analyst.IsAvailable
            && !string.IsNullOrWhiteSpace(notification.Symbol)
            && notification.Kind is NotificationKind.Signal or NotificationKind.Trade;
    }

    public async Task<StrategyNotification> EnrichAsync(StrategyNotification notification, CancellationToken ct)
    {
        var o = _options.CurrentValue.AiAnalyst;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)));

            var bars = await FetchBarsAsync(notification.Symbol, o.BarCount, cts.Token).ConfigureAwait(false);
            if (bars.Count == 0) return notification;

            var request = new AnalystRequest(
                Symbol: notification.Symbol,
                Timeframe: "1h",
                BarCount: bars.Count,
                Provider: o.Provider,
                Model: o.Model,
                VisionModel: o.VisionModel,
                Bars: bars);

            var report = await _analyst.RunAsync(request, cts.Token).ConfigureAwait(false);
            if (report.Decision == AiAnalystDecision.NoCall && string.IsNullOrEmpty(report.PatternChartPngBase64))
            {
                // The Null client / disabled path / timeout path all produce empty PNGs —
                // pass through unchanged rather than spamming "AI Analyst: NoCall" lines.
                return notification;
            }

            var line = $"🤖 AI Analyst: {report.Decision} " +
                $"(conf {report.Confidence:P0}, R:R {report.RiskRewardRatio:F2}) — {report.Pattern.PatternName}";

            return notification with { Message = $"{notification.Message}\n{line}" };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("AI Analyst enrichment timed out — passing through unchanged");
            return notification;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AI Analyst enrichment failed — passing through unchanged");
            return notification;
        }
    }

    private async Task<IReadOnlyList<AnalystBar>> FetchBarsAsync(string symbol, int barCount, CancellationToken ct)
    {
        var connected = _selector.Connected;
        if (connected.Count == 0) return Array.Empty<AnalystBar>();
        var broker = connected[0];

        var contract = Contract.UsStock(symbol);
        var duration = TimeSpan.FromHours(Math.Max(barCount, 1) * 1.2);
        var bars = await _repository.GetHistoricalBarsAsync(contract, broker, BarSize.OneHour, duration, ct).ConfigureAwait(false);
        if (bars.Count == 0) return Array.Empty<AnalystBar>();
        var window = bars.Count <= barCount ? bars : bars.Skip(bars.Count - barCount).ToArray();
        return window.Select(b => new AnalystBar(b.TimestampUtc, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray();
    }
}
