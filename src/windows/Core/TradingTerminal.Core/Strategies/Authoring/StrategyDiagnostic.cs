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
/// <para>
/// <see cref="File"/> names the <see cref="StrategyFile"/> the message points at — a strategy is
/// several files, so a line number alone is ambiguous both to the user and to the model reading its
/// own errors back. Empty for whole-compilation messages (e.g. a policy-scan block).
/// </para>
/// </summary>
public sealed record StrategyDiagnostic(
    StrategyDiagnosticSeverity Severity,
    string Id,
    string Message,
    int Line,
    int Column,
    string File = "")
{
    /// <summary>"File.cs (12,5)" — or just "(12,5)" for a message with no file.</summary>
    public string Location => string.IsNullOrEmpty(File)
        ? $"({Line},{Column})"
        : $"{File} ({Line},{Column})";

    public override string ToString() =>
        $"{Severity} {Id} {Location}: {Message}";
}
