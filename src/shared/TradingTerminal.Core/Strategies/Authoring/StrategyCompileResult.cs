using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// Outcome of compiling a <see cref="StrategyScript"/>. On success, <see cref="Option"/>
/// is a ready-to-run <see cref="BacktestStrategyOption"/> — identical in shape to the
/// in-tree catalog entries, so it can be registered and run/backtested with no special
/// casing. On failure, <see cref="Option"/> is null and <see cref="Diagnostics"/> carries
/// the errors. Warnings may be present even on success.
/// </summary>
public sealed record StrategyCompileResult(
    bool Success,
    BacktestStrategyOption? Option,
    IReadOnlyList<StrategyDiagnostic> Diagnostics)
{
    public IEnumerable<StrategyDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error);

    public static StrategyCompileResult Failed(IReadOnlyList<StrategyDiagnostic> diagnostics) =>
        new(false, null, diagnostics);

    public static StrategyCompileResult Succeeded(
        BacktestStrategyOption option, IReadOnlyList<StrategyDiagnostic> diagnostics) =>
        new(true, option, diagnostics);
}
