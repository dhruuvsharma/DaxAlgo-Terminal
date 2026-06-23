namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>Severity of a <see cref="StrategyDiagnostic"/>.</summary>
public enum StrategyDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One compiler message against an authored <see cref="StrategyScript"/>, mapped to a
/// UI-friendly shape (1-based line/column, no Roslyn types) so the editor can show squiggles
/// and a problems list without referencing the compiler.
/// </summary>
public sealed record StrategyDiagnostic(
    StrategyDiagnosticSeverity Severity,
    string Id,
    string Message,
    int Line,
    int Column)
{
    public override string ToString() =>
        $"{Severity} {Id} ({Line},{Column}): {Message}";
}
