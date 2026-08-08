using DaxAlgo.Sdk;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Sandbox;

/// <summary>An accepted alert routed by the host to its fixed in-view banner destination.</summary>
public sealed record AlertRecord(
    DateTime TimestampUtc,
    string Source,
    AlertLevel Level,
    string Message,
    string? DedupeKey);

/// <summary>
/// Host-mediated alert delivery. Oversized messages and keys are rejected with
/// <see cref="ArgumentException"/>; they are never silently truncated. Accepted alerts are sent
/// only to the injected Activity Log append seam and in-view banner callback.
/// </summary>
public sealed class MediatedAlertSink : IAlertSink
{
    public const int DefaultMaxAlertsPerWindow = 20;

    public static TimeSpan DefaultWindow { get; } = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly string _source;
    private readonly IClock _clock;
    private readonly Action<string, string, string> _appendActivityLog;
    private readonly Action<AlertRecord> _showBanner;
    private readonly TimeSpan _window;
    private readonly int _maxAlertsPerWindow;
    private readonly Queue<DateTime> _acceptedAt = new();
    private readonly Queue<DedupeStamp> _dedupeOrder = new();
    private readonly Dictionary<string, DateTime> _dedupeAt = new(StringComparer.Ordinal);
    private DateTime? _lastObservedUtc;

    public MediatedAlertSink(
        string source,
        IClock clock,
        Action<string, string, string> appendActivityLog,
        Action<AlertRecord> showBanner,
        TimeSpan? window = null,
        int maxAlertsPerWindow = DefaultMaxAlertsPerWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(appendActivityLog);
        ArgumentNullException.ThrowIfNull(showBanner);

        var effectiveWindow = window ?? DefaultWindow;
        if (effectiveWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "Alert window must be positive.");
        if (maxAlertsPerWindow <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxAlertsPerWindow),
                "Alert throttle limit must be positive.");

        _source = source;
        _clock = clock;
        _appendActivityLog = appendActivityLog;
        _showBanner = showBanner;
        _window = effectiveWindow;
        _maxAlertsPerWindow = maxAlertsPerWindow;
    }

    public void Alert(string message, AlertLevel level, string? dedupeKey = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > AlertLimits.MaxMessageLength)
        {
            throw new ArgumentException(
                $"Alert message exceeds the {AlertLimits.MaxMessageLength}-character limit.",
                nameof(message));
        }

        if (dedupeKey?.Length > AlertLimits.MaxDedupeKeyLength)
        {
            throw new ArgumentException(
                $"Alert dedupe key exceeds the {AlertLimits.MaxDedupeKeyLength}-character limit.",
                nameof(dedupeKey));
        }

        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown alert level.");

        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_lastObservedUtc is { } previous && now < previous)
                ClearWindowState();
            _lastObservedUtc = now;

            PruneExpired(now);

            if (!string.IsNullOrEmpty(dedupeKey) && _dedupeAt.ContainsKey(dedupeKey))
                return;

            if (_acceptedAt.Count >= _maxAlertsPerWindow)
                return;

            _acceptedAt.Enqueue(now);
            if (!string.IsNullOrEmpty(dedupeKey))
            {
                _dedupeAt[dedupeKey] = now;
                _dedupeOrder.Enqueue(new DedupeStamp(dedupeKey, now));
            }
        }

        var record = new AlertRecord(now, _source, level, message, dedupeKey);
        Route(record);
    }

    public void AlertIf(bool condition, string message, AlertLevel level, string? dedupeKey = null)
    {
        if (condition)
            Alert(message, level, dedupeKey);
    }

    private void PruneExpired(DateTime now)
    {
        if (now.Ticks < _window.Ticks)
            return;

        var cutoff = new DateTime(now.Ticks - _window.Ticks, now.Kind);
        while (_acceptedAt.Count > 0 && _acceptedAt.Peek() <= cutoff)
            _acceptedAt.Dequeue();

        while (_dedupeOrder.Count > 0 && _dedupeOrder.Peek().TimestampUtc <= cutoff)
        {
            var expired = _dedupeOrder.Dequeue();
            if (_dedupeAt.TryGetValue(expired.Key, out var timestamp) && timestamp == expired.TimestampUtc)
                _dedupeAt.Remove(expired.Key);
        }
    }

    private void ClearWindowState()
    {
        _acceptedAt.Clear();
        _dedupeOrder.Clear();
        _dedupeAt.Clear();
    }

    private void Route(AlertRecord record)
    {
        List<Exception>? failures = null;
        try
        {
            _appendActivityLog(record.Source, LogLevel(record.Level), record.Message);
        }
        catch (Exception ex)
        {
            (failures ??= new List<Exception>()).Add(ex);
        }

        try
        {
            _showBanner(record);
        }
        catch (Exception ex)
        {
            (failures ??= new List<Exception>()).Add(ex);
        }

        if (failures is not null)
            throw new AggregateException("One or more mediated alert routes failed.", failures);
    }

    private static string LogLevel(AlertLevel level) => level switch
    {
        AlertLevel.Information => "INFO",
        AlertLevel.Warning => "WARN",
        AlertLevel.Error => "ERROR",
        AlertLevel.Critical => "CRITICAL",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };

    private readonly record struct DedupeStamp(string Key, DateTime TimestampUtc);
}
