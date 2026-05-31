namespace TradingTerminal.Core.Strategies.Parameters;

/// <summary>
/// Declarative description of a single tunable a strategy exposes. A strategy author
/// builds a list of these (via the static factory helpers) to advertise everything the
/// UI and engine need — without writing a line of XAML or a constructor overload:
/// the editor control, its label, valid range, default, grouping, and help text.
///
/// This is pure metadata. Runtime values live in <see cref="StrategyParameters"/>,
/// validated and clamped against the matching parameter. The pair together replaces the
/// old "hardcoded ctor arg + hand-written view" pattern and is the foundation for
/// author-anything custom strategies.
/// </summary>
public sealed record StrategyParameter
{
    /// <summary>Stable machine key, used to read the value back. Unique within a schema.</summary>
    public required string Key { get; init; }

    /// <summary>Human-friendly label shown next to the editor.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The editor type and value coercion rule.</summary>
    public ParameterKind Kind { get; init; }

    /// <summary>Default value, already of the natural CLR type for <see cref="Kind"/>.</summary>
    public object? Default { get; init; }

    /// <summary>Inclusive minimum for <see cref="ParameterKind.Integer"/> / <see cref="ParameterKind.Number"/>.</summary>
    public double? Min { get; init; }

    /// <summary>Inclusive maximum for numeric kinds.</summary>
    public double? Max { get; init; }

    /// <summary>Increment hint for numeric spinners/sliders. Purely a UI hint.</summary>
    public double? Step { get; init; }

    /// <summary>Allowed values for <see cref="ParameterKind.Choice"/>. Null otherwise.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Optional one-line help shown as a tooltip / sub-label.</summary>
    public string? Description { get; init; }

    /// <summary>Optional group header so related parameters render together (e.g. "Risk").</summary>
    public string? Group { get; init; }

    /// <summary>Optional unit suffix for display (e.g. "bps", "σ", "ms"). Cosmetic only.</summary>
    public string? Unit { get; init; }

    // ── Factory helpers ───────────────────────────────────────────────────────────────
    // Terse, intent-revealing builders so a schema reads like a spec sheet.

    public static StrategyParameter Int(
        string key, string displayName, int @default,
        int? min = null, int? max = null, double step = 1,
        string? group = null, string? unit = null, string? description = null) =>
        new()
        {
            Key = key, DisplayName = displayName, Kind = ParameterKind.Integer,
            Default = (long)@default, Min = min, Max = max, Step = step,
            Group = group, Unit = unit, Description = description,
        };

    public static StrategyParameter Number(
        string key, string displayName, double @default,
        double? min = null, double? max = null, double? step = null,
        string? group = null, string? unit = null, string? description = null) =>
        new()
        {
            Key = key, DisplayName = displayName, Kind = ParameterKind.Number,
            Default = @default, Min = min, Max = max, Step = step,
            Group = group, Unit = unit, Description = description,
        };

    public static StrategyParameter Bool(
        string key, string displayName, bool @default,
        string? group = null, string? description = null) =>
        new()
        {
            Key = key, DisplayName = displayName, Kind = ParameterKind.Boolean,
            Default = @default, Group = group, Description = description,
        };

    public static StrategyParameter Choice(
        string key, string displayName, string @default, IReadOnlyList<string> choices,
        string? group = null, string? description = null) =>
        new()
        {
            Key = key, DisplayName = displayName, Kind = ParameterKind.Choice,
            Default = @default, Choices = choices, Group = group, Description = description,
        };

    public static StrategyParameter Text(
        string key, string displayName, string @default = "",
        string? group = null, string? description = null) =>
        new()
        {
            Key = key, DisplayName = displayName, Kind = ParameterKind.Text,
            Default = @default, Group = group, Description = description,
        };
}
