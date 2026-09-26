using TradingTerminal.Core.Domain;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>
/// A running Blocks strategy's book, as the execution engine copies it: one instrument and the position held in it.
///
/// <para>An authored strategy's model book is already this shape; a Blocks unit keeps a basket (a position per
/// instrument). A book copies one instrument, so the instrument is the one the unit holds, else the one it runs on
/// when it runs on exactly one, else the last one it held — so a strategy that closes out still sends the book its
/// flat target. A unit that watches several instruments and has never held one names none, and the book says so.</para>
///
/// <para>Only the position is copied. Stops, take-profits and pending entries stay inside the unit: they are doubles a
/// broker would reject as inexact prices, and when one fires the position changes and the book follows it.</para>
/// </summary>
public sealed class BlocksModelBook : IModelPortfolioSource, IDisposable
{
    private readonly BlocksUnitRuntime _runtime;
    private InstrumentId _lastHeld;
    private int _disposed;

    public BlocksModelBook(BlocksUnitRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.PortfolioChanged += OnPortfolioChanged;
    }

    public IModelPortfolio? CurrentSnapshot => Project(_runtime.Portfolio);

    public event Action<IModelPortfolio>? SnapshotChanged;

    /// <summary>Publishes the current book, for when the unit has started (its instruments are known only then).</summary>
    public void Refresh()
    {
        if (Volatile.Read(ref _disposed) == 0)
            SnapshotChanged?.Invoke(Project(_runtime.Portfolio));
    }

    private void OnPortfolioChanged(PortfolioSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) == 0)
            SnapshotChanged?.Invoke(Project(snapshot));
    }

    private IModelPortfolio Project(PortfolioSnapshot snapshot)
    {
        var held = snapshot.Positions.Where(position => position.Units != 0d).ToArray();
        var running = _runtime.SubscribedInstruments;
        InstrumentId instrument;
        if (held.Length == 1)
            instrument = held[0].Instrument;
        else if (held.Length > 1)
            instrument = default; // a basket: no single instrument a book could copy
        else if (running.Count == 1)
            instrument = running.First();
        else
            instrument = _lastHeld;

        if (held.Length == 1)
            _lastHeld = held[0].Instrument;

        var position = held.Length == 1 ? held[0] : null;
        var account = snapshot.Account;
        return new SandboxPortfolioSnapshot(
            instrument,
            position?.Units ?? 0d,
            position?.Units ?? 0d,
            position?.AveragePrice ?? 0d,
            BarsHeld: 0,
            account.Equity,
            account.RealizedPnl,
            account.Commission,
            SlippageTotal: 0d,
            EquityPeak: account.Equity,
            MaximumDrawdown: 0d,
            LifetimeClosedTripCount: 0,
            LifetimeWinningTripCount: 0,
            LifetimeLosingTripCount: 0,
            RetainedTradeCount: 0,
            Streak: 0,
            IsComplete: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _runtime.PortfolioChanged -= OnPortfolioChanged;
    }
}
