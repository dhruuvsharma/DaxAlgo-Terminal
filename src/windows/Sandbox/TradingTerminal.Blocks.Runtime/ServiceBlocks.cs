using System.Text;
using System.Text.Json;
using DaxAlgo.Blocks;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>The settings block, over the same coercion and validation the parameter panel uses.</summary>
internal sealed class SettingsBlock(StrategyParameterSchema schema, IReadOnlyDictionary<string, object?>? values, UnitThread thread)
    : ISettings
{
    private StrategyParameters _values = new(schema, values);
    private readonly List<Action<string>> _changed = [];
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    public StrategyParameterSchema Schema => schema;

    public IReadOnlyDictionary<string, object?> Values => _values.ToDictionary();

    /// <summary>Declared settings the unit has never read — a control on the panel that does nothing.</summary>
    public IReadOnlyList<string> Unread
    {
        get { lock (_read) return [.. schema.Parameters.Select(p => p.Key).Where(key => !_read.Contains(key))]; }
    }

    public int Int(string key) => _values.GetInt(Read(key));

    public double Number(string key) => _values.GetDouble(Read(key));

    public bool Bool(string key) => _values.GetBool(Read(key));

    public string Text(string key) => _values.GetString(Read(key));

    public InstrumentId Instrument(string key) => _values.GetInstrument(Read(key));

    private string Read(string key)
    {
        lock (_read) _read.Add(key);
        return key;
    }

    public void Set(string key, object value)
    {
        // Validated on a copy, so a refused value leaves the running unit on the value it had.
        var candidate = new StrategyParameters(schema, _values.ToDictionary());
        candidate.Set(key, value);

        var errors = candidate.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(value));

        if (Equals(_values.GetRaw(key), candidate.GetRaw(key))) return;
        _values = candidate;

        // Posted, never called inline: a handler that sets another setting would otherwise re-enter.
        thread.Post(() =>
        {
            foreach (var handler in _changed.ToArray()) handler(key);
        });
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _changed.Add(handler);
        return new Disposer(() => _changed.Remove(handler));
    }
}

/// <summary>Timers and posting onto the unit thread.</summary>
internal sealed class ScheduleBlock(UnitThread thread) : ISchedule, IDisposable
{
    internal static readonly TimeSpan Minimum = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly HashSet<Timer> _timers = [];
    private bool _disposed;

    public IDisposable Every(TimeSpan interval, Action work) => Start(interval, work, repeat: true);

    public IDisposable After(TimeSpan delay, Action work) => Start(delay, work, repeat: false);

    public void Post(Action work) => thread.Post(work);

    private IDisposable Start(TimeSpan due, Action work, bool repeat)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (due < Minimum) due = Minimum;

        // A tick is queued at most once at a time. If the work is slower than the interval, ticks are
        // skipped rather than stacking up in the queue for ever.
        var queued = 0;
        Timer? timer = null;

        timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref queued, 1) == 1) return;
            thread.Post(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                work();
            });

            if (!repeat) Stop(timer!);
        });

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timers.Add(timer);
        }

        timer.Change(due, repeat ? due : Timeout.InfiniteTimeSpan);
        return new Disposer(() => Stop(timer));
    }

    private void Stop(Timer timer)
    {
        lock (_gate)
            if (!_timers.Remove(timer)) return;

        timer.Dispose();
    }

    public void Dispose()
    {
        Timer[] timers;
        lock (_gate)
        {
            _disposed = true;
            timers = [.. _timers];
            _timers.Clear();
        }

        foreach (var timer in timers) timer.Dispose();
    }
}

/// <summary>The unit's page: coalesced sends, and messages back onto the unit thread.</summary>
internal sealed class UiBlock : IUiBridge, IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int MaximumTopicLength = 64;

    private readonly IUnitUiEndpoint? _endpoint;
    private readonly UnitThread _thread;
    private readonly TimeSpan _flush;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<JsonElement>>> _handlers = new(StringComparer.Ordinal);
    private readonly List<Action> _opened = [];
    private readonly Timer _timer;
    private bool _scheduled;

    public UiBlock(IUnitUiEndpoint? endpoint, UnitThread thread, TimeSpan flush, Action<string> warn)
    {
        _endpoint = endpoint;
        _thread = thread;
        _flush = flush;
        _warn = warn;
        _timer = new Timer(_ => Flush());

        if (_endpoint is not null)
        {
            _endpoint.MessageReceived += OnMessage;
            _endpoint.Opened += OnOpened;
        }
    }

    public bool IsOpen => _endpoint?.IsOpen == true;

    public void Send(string topic, object? payload)
    {
        RequireTopic(topic);
        if (_endpoint is null) return;

        // Serialized now, on the unit thread, so a payload the unit mutates afterwards is sent as it was.
        var json = JsonSerializer.Serialize(payload, Json);

        lock (_gate)
        {
            _latest[topic] = json;
            if (_scheduled) return;
            _scheduled = true;
        }

        _timer.Change(_flush, Timeout.InfiniteTimeSpan);
    }

    public IDisposable On(string topic, Action<JsonElement> handler)
    {
        RequireTopic(topic);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_handlers.TryGetValue(topic, out var list)) _handlers[topic] = list = [];
            list.Add(handler);
        }

        return new Disposer(() =>
        {
            lock (_gate)
                if (_handlers.TryGetValue(topic, out var list)) list.Remove(handler);
        });
    }

    public IDisposable OnOpened(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _opened.Add(handler);

        // A PAGE CAN BE READY BEFORE THE UNIT LISTENS. A fast page calls dax.ready() while the window is
        // still starting the runtime, the Opened event fires with nobody attached, and the unit — waiting
        // to be told the page is open — never sends it anything. Found by the page probe, whose page won
        // that race on a warm browser. So a handler registered on an open page runs at once. At worst it
        // runs twice around the moment the page opens, which is harmless: it sends whole state.
        if (IsOpen) _thread.Post(handler);

        return new Disposer(() => { lock (_gate) _opened.Remove(handler); });
    }

    private void Flush()
    {
        KeyValuePair<string, string>[] pending;
        lock (_gate)
        {
            pending = [.. _latest];
            _latest.Clear();
            _scheduled = false;
        }

        if (_endpoint is not { IsOpen: true }) return;

        foreach (var (topic, json) in pending)
        {
            try { _endpoint.Post(topic, json); }
            catch (Exception ex) { _warn($"The page could not be sent '{topic}': {ex.Message}"); }
        }
    }

    private void OnMessage(string topic, string json) => _thread.Post(() =>
    {
        Action<JsonElement>[] handlers;
        lock (_gate)
            handlers = _handlers.TryGetValue(topic, out var list) ? [.. list] : [];
        if (handlers.Length == 0) return;

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            _warn($"The page sent '{topic}' with a payload that is not JSON; it was ignored.");
            return;
        }

        foreach (var handler in handlers) handler(payload);
    });

    private void OnOpened() => _thread.Post(() =>
    {
        Action[] handlers;
        lock (_gate) handlers = [.. _opened];
        foreach (var handler in handlers) handler();
    });

    private static void RequireTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > MaximumTopicLength)
            throw new ArgumentException($"A topic is 1 to {MaximumTopicLength} characters.", nameof(topic));
    }

    public void Dispose()
    {
        if (_endpoint is not null)
        {
            _endpoint.MessageReceived -= OnMessage;
            _endpoint.Opened -= OnOpened;
        }

        _timer.Dispose();
    }
}

/// <summary>Values that survive restarts, bounded and JSON-serialized.</summary>
internal sealed class StateBlock : IState
{
    private readonly string _unitId;
    private readonly IUnitStateStore? _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values;
    private int _bytes;
    private bool _dirty;

    public StateBlock(string unitId, IUnitStateStore? store)
    {
        _unitId = unitId;
        _store = store;
        _values = new Dictionary<string, string>(store?.Load(unitId) ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        _bytes = _values.Sum(pair => Size(pair.Key, pair.Value));
    }

    public T? Get<T>(string key)
    {
        lock (_gate)
            return _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, UiBlock.Json) : default;
    }

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var json = JsonSerializer.Serialize(value, UiBlock.Json);

        lock (_gate)
        {
            var total = _bytes - (_values.TryGetValue(key, out var old) ? Size(key, old) : 0) + Size(key, json);
            if (total > IState.MaxBytes)
                throw new InvalidOperationException($"State would grow to {total:N0} bytes; the limit is {IState.MaxBytes:N0}.");

            _values[key] = json;
            _bytes = total;
            _dirty = true;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key, out var old)) return false;
            _bytes -= Size(key, old);
            _dirty = true;
            return true;
        }
    }

    public IReadOnlyCollection<string> Keys
    {
        get { lock (_gate) return [.. _values.Keys]; }
    }

    /// <summary>Saves when anything changed since the last save. Returns the failure, if any.</summary>
    public string? Save()
    {
        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            if (!_dirty || _store is null) return null;
            snapshot = new Dictionary<string, string>(_values, StringComparer.Ordinal);
            _dirty = false;
        }

        try
        {
            _store.Save(_unitId, snapshot);
            return null;
        }
        catch (Exception ex)
        {
            lock (_gate) _dirty = true;
            return ex.Message;
        }
    }

    private static int Size(string key, string json) => Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(json);
}

internal sealed class ClockBlock(IClock clock) : IUnitClock
{
    public DateTime UtcNow => clock.UtcNow;
}

internal sealed class AlertsBlock(IAlertSink sink) : IAlerts
{
    public void Raise(string message, AlertSeverity severity = AlertSeverity.Info, string? dedupeKey = null) =>
        sink.Alert(message, severity switch
        {
            AlertSeverity.Warning => AlertLevel.Warning,
            AlertSeverity.Critical => AlertLevel.Critical,
            _ => AlertLevel.Information,
        }, dedupeKey);
}

internal sealed class LogBlock(string source, Action<string, string, string> append) : ILog
{
    private const int MaximumLength = 512;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        message ??= string.Empty;
        try { append(source, level, message.Length <= MaximumLength ? message : message[..MaximumLength]); }
        catch { /* the activity log failing must not fail the unit */ }
    }
}

internal sealed class ExportBlock(Func<string, string, bool>? offer) : IExport
{
    public bool Offer(string label, string text)
    {
        if (offer is null || string.IsNullOrWhiteSpace(label) || label.Length > 64 || text is null || text.Length > 262_144)
            return false;

        try { return offer(label, text); }
        catch { return false; }
    }
}

internal sealed class HistoryBlock(IMarketDataStore? store) : IHistory
{
    public async Task<IReadOnlyList<OhlcvBar>> BarsAsync(
        InstrumentId instrument, BarSize size, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (store is null) return [];
        var bars = new List<OhlcvBar>();
        await foreach (var bar in store.ReadBarsAsync(instrument, size, fromUtc, toUtc, ct: ct).ConfigureAwait(false))
        {
            bars.Add(bar);
            if (bars.Count >= IHistory.MaxItems) break;
        }

        return bars;
    }

    public async Task<IReadOnlyList<TradePrint>> TradesAsync(
        InstrumentId instrument, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (store is null) return [];
        var trades = new List<TradePrint>();
        await foreach (var trade in store.ReadTradesAsync(instrument, fromUtc, toUtc, ct: ct).ConfigureAwait(false))
        {
            trades.Add(trade);
            if (trades.Count >= IHistory.MaxItems) break;
        }

        return trades;
    }
}

internal sealed class InstrumentsBlock(
    Func<InstrumentId, Instrument?>? find,
    Func<string, int, IReadOnlyList<Instrument>>? search) : IInstruments
{
    public Instrument? Find(InstrumentId instrument) => find?.Invoke(instrument);

    public IReadOnlyList<Instrument> Search(string text, int max = 20) =>
        search is null || string.IsNullOrWhiteSpace(text) ? [] : search(text, Math.Clamp(max, 1, 200));
}
