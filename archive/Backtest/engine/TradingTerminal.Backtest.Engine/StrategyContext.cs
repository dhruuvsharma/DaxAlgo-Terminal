using TradingTerminal.Backtest.Engine.Accounting;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine;

/// <summary>The engine's <see cref="IStrategyContext"/> — a thin bundle of the per-run services
/// handed to the kernel. Read-only from the kernel's side except via the router.</summary>
internal sealed class StrategyContext : IStrategyContext
{
    public StrategyContext(IClock clock, IOrderRouter router, IPortfolioView portfolio, Universe universe, StrategyParameters parameters)
    {
        Clock = clock;
        Router = router;
        Portfolio = portfolio;
        Universe = universe;
        Parameters = parameters;
    }

    public IClock Clock { get; }
    public IOrderRouter Router { get; }
    public IPortfolioView Portfolio { get; }
    public Universe Universe { get; }
    public StrategyParameters Parameters { get; }
}

/// <summary>Adapts the engine's mutable <see cref="Portfolio"/> to the kernel-facing read-only
/// <see cref="IPortfolioView"/>, so kernels can query positions but only change them via orders.</summary>
internal sealed class PortfolioView : IPortfolioView
{
    private readonly Portfolio _portfolio;

    public PortfolioView(Portfolio portfolio) => _portfolio = portfolio;

    public double Cash => _portfolio.Cash;
    public double Equity => _portfolio.Equity();
    public Position PositionOf(InstrumentId instrument) => _portfolio.SnapshotOf(instrument);
    public IReadOnlyCollection<Position> OpenPositions => _portfolio.OpenPositions();
}
