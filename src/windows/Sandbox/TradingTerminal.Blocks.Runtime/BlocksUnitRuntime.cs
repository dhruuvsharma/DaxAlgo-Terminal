using DaxAlgo.Blocks;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Sandbox;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>An immutable copy of a unit's portfolio.</summary>
public sealed record PortfolioSnapshot(AccountView Account, IReadOnlyList<PositionView> Positions)
{
    public static PortfolioSnapshot Empty { get; } = new(new AccountView(BasketBook.StartingEquity, 0d, 0d, 0d), []);
}

/// <summary>Where a runtime is in its life.</summary>
public enum UnitRunState
{
    Created,
    Running,
    Stopped,
    Faulted,
}

/// <summary>
/// Runs one DaxAlgo.Blocks unit: builds its blocks, starts it on its own thread, delivers market data,
/// works its orders, and stops it cleanly.
///
/// <para>Every handler the unit registers — data, timers, page messages, posted work — runs on the
/// unit thread, one at a time. A handler that throws is logged and skipped; the unit keeps running,
/// because one bad tick is not a reason to close somebody's window.</para>
/// </summary>
public sealed class BlocksUnitRuntime : IAsyncDisposable
{
    private readonly Func<IUnit> _factory;
    private readonly string _unitId;
    private readonly BlocksHost _host;
    private readonly IReadOnlyDictionary<string, object?>? _settings;
    private readonly IUnitUiEndpoint? _endpoint;
    private readonly BlocksRuntimeOptions _options;
    private readonly UnitThread _thread;

    private MarketBlock? _market;
    private ScheduleBlock? _schedule;
    private UiBlock? _ui;
    private StateBlock? _state;
    private BasketBook? _book;
    private SettingsBlock? _settingsBlock;
    private LogBlock? _log;
    private Timer? _saveTimer;
    private long _faults;

    public BlocksUnitRuntime(
        Func<IUnit> factory,
        string unitId,
        BlocksHost host,
        IReadOnlyDictionary<string, object?>? settings = null,
        IUnitUiEndpoint? ui = null,
        BlocksRuntimeOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        _unitId = unitId;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings;
        _endpoint = ui;
        _options = options ?? new BlocksRuntimeOptions();
        _thread = new UnitThread(
            _options.MarketQueueCapacity, OnMarket, Fault, _options.ControlQueueCapacity,
            refused => _log?.Warn($"The unit is not keeping up with its own work: {refused:N0} posted item(s) refused. "
                                  + "Something is posting faster than the handlers run — a page flooding messages, or a loop posting results."));
    }

    public UnitRunState State { get; private set; } = UnitRunState.Created;

    /// <summary>The running unit, once started.</summary>
    public IUnit? Unit { get; private set; }

    /// <summary>The unit's name, or the id until it has started.</summary>
    public string Name => Unit?.Info.Name is { Length: > 0 } name ? name : _unitId;

    /// <summary>True once the unit has used the orders block, which makes it a strategy.</summary>
    public bool UsesOrders => _book?.Touched == true;

    /// <summary>Market events dropped because the unit fell behind.</summary>
    public long DroppedMarketEvents => _thread.DroppedMarketEvents;

    /// <summary>Posted work refused because the unit's work queue was full.</summary>
    public long RefusedWork => _thread.RefusedWork;

    /// <summary>Handlers that threw and were skipped.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>The settings schema the unit declared, once started.</summary>
    public StrategyParameterSchema? Schema => _settingsBlock?.Schema;

    /// <summary>Declared settings the unit has not read so far.</summary>
    public IReadOnlyList<string> UnreadSettings => _settingsBlock?.Unread ?? [];

    /// <summary>Instruments the unit has a live stream open for.</summary>
    public IReadOnlyCollection<TradingTerminal.Core.Domain.InstrumentId> SubscribedInstruments => _market?.Instruments ?? [];

    /// <summary>The most recent fills, oldest first. Safe from any thread.</summary>
    public IReadOnlyList<FillView> RecentFills(int count) => _book?.RecentFills(count) ?? [];

    /// <summary>The message of the last handler failure, if any.</summary>
    public string? LastFault { get; private set; }

    /// <summary>
    /// The portfolio as of the last change, safe to read from any thread. The blocks themselves belong
    /// to the unit thread; this is the copy the host's window reads.
    /// </summary>
    public PortfolioSnapshot Portfolio => Volatile.Read(ref _portfolio);

    private PortfolioSnapshot _portfolio = PortfolioSnapshot.Empty;

    /// <summary>Raised on the unit thread after the portfolio changed.</summary>
    public event Action<PortfolioSnapshot>? PortfolioChanged;

    /// <summary>Creates the unit, builds its blocks and runs its StartAsync on the unit thread.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State != UnitRunState.Created)
            throw new InvalidOperationException($"A runtime starts once; this one is {State}.");

        var unit = _factory() ?? throw new InvalidOperationException("The unit factory returned null.");
        Unit = unit;

        var info = unit.Info ?? throw new InvalidOperationException("The unit's Info is null.");
        var schema = info.Settings is { Count: > 0 } declared ? new StrategyParameterSchema(declared) : StrategyParameterSchema.Empty;

        var source = string.IsNullOrWhiteSpace(info.Name) ? _unitId : info.Name;
        _log = new LogBlock(source, _host.AppendActivityLog);
        _settingsBlock = new SettingsBlock(schema, _settings, _thread);
        _market = new MarketBlock(_host.Hub, _thread, _options.RecentWindow, Fault, _host.OpenFeed, _log.Warn);
        _schedule = new ScheduleBlock(_thread);
        _ui = new UiBlock(_endpoint, _thread, _options.UiFlush, _log.Warn);
        _state = new StateBlock(_unitId, _host.StateStore);
        _book = new BasketBook(() => _host.Clock.UtcNow, _log.Warn);

        var context = new UnitContext(
            _settingsBlock,
            _market,
            new HistoryBlock(_host.Store),
            new InstrumentsBlock(_host.FindInstrument, _host.SearchInstruments),
            new ClockBlock(_host.Clock),
            _schedule,
            _book,
            _book,
            new AlertsBlock(new MediatedAlertSink(source, _host.Clock, _host.AppendActivityLog, _host.ShowBanner)),
            _log,
            _state,
            new ExportBlock(_host.OfferExport),
            _ui);

        _thread.Start();

        try
        {
            await _thread.InvokeAsync(() => unit.StartAsync(context, ct)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            State = UnitRunState.Faulted;
            _log.Error($"The unit failed to start: {ex.GetType().Name}: {ex.Message}");
            await ReleaseAsync().ConfigureAwait(false);
            throw;
        }

        _saveTimer = new Timer(_ => SaveState(), null, _options.StateSave, _options.StateSave);
        State = UnitRunState.Running;
    }

    /// <summary>Applies new setting values from the host's panel; the unit hears each change.</summary>
    public Task ApplySettingsAsync(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var settings = _settingsBlock ?? throw new InvalidOperationException("Start the runtime first.");

        return _thread.InvokeAsync(() =>
        {
            foreach (var (key, value) in values)
                if (value is not null) settings.Set(key, value);
            return Task.CompletedTask;
        });
    }

    /// <summary>Runs the unit's StopAsync, releases its streams and timers, and saves its state.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (State != UnitRunState.Running) return;

        try
        {
            await _thread.InvokeAsync(() => Unit!.StopAsync(ct)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Error($"The unit failed to stop cleanly: {ex.GetType().Name}: {ex.Message}");
        }

        State = UnitRunState.Stopped;
        await ReleaseAsync().ConfigureAwait(false);
    }

    private void OnMarket(MarketEvent evt)
    {
        _market!.Dispatch(evt);

        if (_book!.OnPrice(evt))
        {
            var snapshot = new PortfolioSnapshot(_book.Account, _book.Positions);
            Volatile.Write(ref _portfolio, snapshot);

            try { PortfolioChanged?.Invoke(snapshot); }
            catch (Exception ex) { _log?.Warn($"A portfolio listener failed: {ex.Message}"); }
        }
    }

    private void Fault(Exception ex)
    {
        Interlocked.Increment(ref _faults);
        LastFault = $"{ex.GetType().Name}: {ex.Message}";
        _log?.Error($"A handler failed and was skipped: {ex.GetType().Name}: {ex.Message}");
    }

    private void SaveState()
    {
        if (_state?.Save() is { } failure) _log?.Warn($"State could not be saved: {failure}");
    }

    private async Task ReleaseAsync()
    {
        _saveTimer?.Dispose();
        _schedule?.Dispose();
        _market?.Dispose();
        _ui?.Dispose();
        SaveState();
        await _thread.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (State == UnitRunState.Running) await StopAsync().ConfigureAwait(false);
        else if (State == UnitRunState.Created) await _thread.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record UnitContext(
        ISettings Settings,
        IMarketData Market,
        IHistory History,
        IInstruments Instruments,
        IUnitClock Clock,
        ISchedule Schedule,
        IOrders Orders,
        IPortfolio Portfolio,
        IAlerts Alerts,
        ILog Log,
        IState State,
        IExport Export,
        IUiBridge Ui) : IUnitContext;
}
