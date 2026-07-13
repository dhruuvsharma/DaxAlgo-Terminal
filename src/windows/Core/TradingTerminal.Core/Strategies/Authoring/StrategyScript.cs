namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// One C# file of an authored strategy. A strategy is a small project, not a blob: the kernel lives in
/// one file, indicators/helpers in others, and (for a plugin) the catalog descriptor, the live
/// view-model and the view in their own. <paramref name="Name"/> is a bare file name
/// (<c>MyStrategy.cs</c>) used as the compilation path, so a diagnostic points at the file the user is
/// looking at.
/// </summary>
public sealed record StrategyFile(string Name, string Content)
{
    /// <summary>The name given to a single unnamed file (the AI didn't label its block).</summary>
    public const string DefaultName = "Strategy.cs";
}

/// <summary>
/// A user-authored strategy awaiting compilation: a stable id, a friendly display name, and one or more
/// C# files that together define a single public class implementing <see cref="Backtest.IBacktestStrategy"/>
/// with a public <c>(Contract)</c> constructor.
///
/// The class may optionally expose a <c>public static StrategyParameterSchema Schema</c> and a
/// <c>public static T Create(Contract, StrategyParameters)</c> to participate in the declarative-parameter
/// system — exactly like the in-tree strategies. This is the entry point for authoring a strategy without
/// a project or a recompile.
/// </summary>
public sealed record StrategyScript(
    string Id,
    string DisplayName,
    IReadOnlyList<StrategyFile> Files)
{
    /// <summary>Single-file convenience — the shape most callers (and the starter template) use.</summary>
    public StrategyScript(string id, string displayName, string sourceCode)
        : this(id, displayName, [new StrategyFile(StrategyFile.DefaultName, sourceCode)])
    {
    }
}
