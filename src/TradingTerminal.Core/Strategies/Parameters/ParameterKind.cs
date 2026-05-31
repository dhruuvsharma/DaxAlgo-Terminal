namespace TradingTerminal.Core.Strategies.Parameters;

/// <summary>
/// The editable type of a <see cref="StrategyParameter"/>. Drives both runtime value
/// coercion in <see cref="StrategyParameters"/> and the editor control the UI renders
/// (a slider/spinner for numbers, a checkbox for booleans, a combo box for choices,
/// a text box for free text). Keep this enum broker- and UI-neutral: it lives in Core.
/// </summary>
public enum ParameterKind
{
    /// <summary>Whole number. Backed by <see cref="long"/>; exposed via <c>GetInt</c>.</summary>
    Integer,

    /// <summary>Real number. Backed by <see cref="double"/>; exposed via <c>GetDouble</c>.</summary>
    Number,

    /// <summary>True/false toggle. Backed by <see cref="bool"/>; exposed via <c>GetBool</c>.</summary>
    Boolean,

    /// <summary>One of a fixed set of <see cref="StrategyParameter.Choices"/>. Backed by <see cref="string"/>.</summary>
    Choice,

    /// <summary>Free-form text. Backed by <see cref="string"/>.</summary>
    Text,
}
