namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// A user-authored strategy awaiting compilation: a stable id, a friendly display name,
/// and C# source that defines a single public class implementing
/// <see cref="Backtest.IBacktestStrategy"/> with a public <c>(Contract)</c> constructor.
///
/// The class may optionally expose a <c>public static StrategyParameterSchema Schema</c>
/// and a <c>public static T Create(Contract, StrategyParameters)</c> to participate in the
/// declarative-parameter system — exactly like the in-tree strategies. This is the entry
/// point for authoring a strategy without a project or a recompile.
/// </summary>
public sealed record StrategyScript(
    string Id,
    string DisplayName,
    string SourceCode);
