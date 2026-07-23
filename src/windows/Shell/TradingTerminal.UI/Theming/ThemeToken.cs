using System.Collections.Generic;
using System.Windows.Media;

namespace TradingTerminal.UI.Theming;

/// <summary>Whether a <see cref="ThemeToken"/> is a single flat colour or a multi-stop gradient.</summary>
public enum ThemeTokenKind
{
    /// <summary>A flat colour — a <c>SolidColorBrush</c> and/or a <c>Color</c> resource.</summary>
    Solid,

    /// <summary>A <c>LinearGradientBrush</c> — edited stop-by-stop, geometry preserved.</summary>
    Gradient,
}

/// <summary>
/// One editable palette entry surfaced to the Theme Studio. A token is discovered by reflecting over
/// the active palette dictionary (<see cref="IThemeManager.EnumerateTokens"/>): every
/// <c>SolidColorBrush</c> / <c>Color</c> / <c>LinearGradientBrush</c> key becomes one token, grouped
/// for display and (for solids) paired with its sibling <c>.Color</c> key so editing one row drives
/// both the brush and the raw colour.
/// </summary>
/// <param name="DisplayName">Humanised label for the editor row (e.g. "Background Primary").</param>
/// <param name="Group">UI grouping bucket (e.g. "Backgrounds", "Semantic (P&amp;L / status)").</param>
/// <param name="Kind">Solid vs gradient.</param>
/// <param name="PrimaryKey">The resource key written on edit (a brush key, or a standalone colour key).</param>
/// <param name="LinkedColorKey">Optional sibling <c>.Color</c> key kept in sync with <paramref name="PrimaryKey"/>.</param>
/// <param name="SolidValue">Current effective colour (for <see cref="ThemeTokenKind.Solid"/>).</param>
/// <param name="GradientStops">Current effective stop colours (for <see cref="ThemeTokenKind.Gradient"/>).</param>
public sealed record ThemeToken(
    string DisplayName,
    string Group,
    ThemeTokenKind Kind,
    string PrimaryKey,
    string? LinkedColorKey,
    Color SolidValue,
    IReadOnlyList<Color> GradientStops);

/// <summary>
/// On-disk representation of a custom theme — a built-in base palette plus a full snapshot of colour
/// and gradient overrides. Self-contained (every token is captured), so a custom theme file applies
/// the same way regardless of which tokens the designer actually touched. Serialised as JSON; colours
/// are <c>#AARRGGBB</c> strings. Shareable: export to a <c>.json</c>, import on another machine.
/// </summary>
public sealed class CustomThemeFile
{
    /// <summary>Display name shown in the View → Theme menu.</summary>
    public string Name { get; set; } = "Custom";

    /// <summary>Id of the built-in DaxAlgo palette this theme starts from.</summary>
    public string BaseThemeId { get; set; } = "daxalgo-dark";

    /// <summary>Resource key → <c>#AARRGGBB</c>. Covers brush keys and their sibling colour keys.</summary>
    public Dictionary<string, string> Colors { get; set; } = new();

    /// <summary>Gradient resource key → ordered list of stop colours (<c>#AARRGGBB</c>).</summary>
    public Dictionary<string, List<string>> Gradients { get; set; } = new();
}
