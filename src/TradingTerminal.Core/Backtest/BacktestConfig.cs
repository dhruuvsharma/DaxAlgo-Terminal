using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// Inputs for a single backtest run. <see cref="ContractMultiplier"/> scales price moves
/// to dollars (e.g. 50 for ES, 1 for stocks); <see cref="SlippageTicks"/> is added to the
/// touch on market fills to model crossing the spread under load. <see cref="FeeModel"/>
/// is consulted on every fill; defaults to <see cref="ZeroFeeModel"/> so legacy backtests
/// reproduce exactly.
/// </summary>
public sealed record BacktestConfig(
    Contract Contract,
    string TickDataPath,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    double TickSize = 0.25,
    int SlippageTicks = 0,
    double ContractMultiplier = 1.0,
    double StartingCash = 100_000d,
    IFeeModel? FeeModel = null);
