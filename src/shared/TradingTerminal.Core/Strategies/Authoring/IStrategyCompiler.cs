namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Compiles a user-authored <see cref="StrategyScript"/> into a runnable
/// <see cref="Backtest.IBacktestStrategy"/> at runtime — no project, no recompile of the
/// host. The concrete compiler (Roslyn) lives in Infrastructure so no compiler types leak
/// into Core or UI; consumers depend only on this seam.
///
/// <para><b>Trust boundary:</b> compiled scripts run in-process with full host privileges.
/// This is a local, single-user desktop tool and the data/signals build carries no live
/// order-execution path, so an authored strategy is no more privileged than a .cs file the
/// user would otherwise add and recompile. Implementations must still fail loudly on
/// compile errors and never partially register a broken strategy.</para>
/// </summary>
public interface IStrategyCompiler
{
    StrategyCompileResult Compile(StrategyScript script);
}
