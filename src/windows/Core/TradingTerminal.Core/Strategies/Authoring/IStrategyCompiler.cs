namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Compiles a user-authored <see cref="StrategyScript"/> into a runnable
/// <see cref="Backtest.IBacktestStrategy"/> at runtime — no project, no recompile of the
/// host. The concrete compiler (Roslyn) lives in Infrastructure so no compiler types leak
/// into Core or UI; consumers depend only on this seam.
///
/// <para><b>Trust boundary:</b> compiled scripts run in-process with full host privileges — and the
/// author isn't always the user (an AI builder, or a pasted snippet, writes source through this same
/// seam). Implementations MUST run the emitted image through the plugin policy scan before loading it
/// (the same gate a dropped-in plugin passes) so Block-level capabilities — P/Invoke, starting
/// processes, the registry, <c>Reflection.Emit</c>, loading assemblies — fail the compile instead of
/// executing. They must also fail loudly on compile errors and never partially register a broken
/// strategy. The data/signals build carries no live order-execution path, which bounds the blast
/// radius but does not make authored code trusted.</para>
/// </summary>
public interface IStrategyCompiler
{
    StrategyCompileResult Compile(StrategyScript script);
}
