using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Projects a strategy's <see cref="StrategyDataRequirement"/> (or an
/// <see cref="ITradingStrategy"/> whose <c>DataRequirement</c> is read automatically) into an
/// ordered <see cref="List{T}"/> of <see cref="InstrumentTag"/> pills that match the look of the
/// instrument capability pills rendered by <see cref="InstrumentTagsConverter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Accepted input types:
/// <list type="bullet">
///   <item><see cref="StrategyDataRequirement"/> — used directly.</item>
///   <item><see cref="ITradingStrategy"/> — <c>DataRequirement</c> is read from the instance.</item>
/// </list>
/// Any other value returns an empty list.
/// </para>
/// <para>
/// Pill order is always: L1 → BAR → L2 → TAPE (only set flags are emitted).
/// L1 and BAR use the same muted slate background as the instrument data-capability pills
/// (<c>#334155</c>/<c>#CFD8DC</c>). L2 and TAPE — the "informative extras" that not every broker
/// can serve — use the theme's amber-dim accent (<c>Accent.Dim.Color</c> → <c>#A65A00</c>) so they
/// stand out visually without using a warning/danger colour.
/// </para>
/// <para>
/// Register the single shared instance in <see cref="Application"/> resources by calling
/// <see cref="EnsureConverterRegistered"/> (done automatically by any UI that references the
/// resource key <see cref="ConverterKey"/> — <c>"StrategyTagsConverter"</c>) before the XAML
/// template is realised. Mirrors the <c>EnsureConverterRegistered</c> pattern in
/// <see cref="TradingTerminal.UI.Controls.InstrumentPicker"/>.
/// </para>
/// </remarks>
public sealed class StrategyDataRequirementConverter : IValueConverter
{
    /// <summary>
    /// Resource key under which the shared <see cref="StrategyDataRequirementConverter"/> is
    /// registered in <see cref="Application"/> resources.
    /// XAML usage: <c>Converter="{StaticResource StrategyTagsConverter}"</c>.
    /// </summary>
    public const string ConverterKey = "StrategyTagsConverter";

    // ── Baseline data pills — same slate as InstrumentTagsConverter.DataBg/DataFg ────────────
    private static readonly Brush BaselineBg = MakeBrush("#334155");  // muted slate
    private static readonly Brush BaselineFg = MakeBrush("#CFD8DC");

    // ── Extra-data pills — subdued active-theme accent (Accent.Dim.Color) ───────────────────
    // Signals "this strategy needs data not every broker provides" without alarm-colouring.
    private static readonly Brush ExtraBg = MakeBrush("#A65A00");     // Accent.Dim.Color
    private static readonly Brush ExtraFg = MakeBrush("#FFE0A0");     // warm off-white over amber-dim

    // ── Pre-built pills (frozen brushes — cheap to share across many rows) ────────────────────
    private static readonly InstrumentTag PillL1   = new("L1",   BaselineBg, BaselineFg);
    private static readonly InstrumentTag PillBar  = new("BAR",  BaselineBg, BaselineFg);
    private static readonly InstrumentTag PillL2   = new("L2",   ExtraBg,    ExtraFg);
    private static readonly InstrumentTag PillTape = new("TAPE", ExtraBg,    ExtraFg);

    // ── IValueConverter ───────────────────────────────────────────────────────────────────────

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var req = value switch
        {
            StrategyDataRequirement r  => r,
            ITradingStrategy s         => s.DataRequirement,
            _                          => (StrategyDataRequirement?)null,
        };

        if (req is null) return new List<InstrumentTag>();

        var tags = new List<InstrumentTag>(4);

        if (req.Value.HasFlag(StrategyDataRequirement.L1))        tags.Add(PillL1);
        if (req.Value.HasFlag(StrategyDataRequirement.Bars))      tags.Add(PillBar);
        if (req.Value.HasFlag(StrategyDataRequirement.Depth))     tags.Add(PillL2);
        if (req.Value.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add(PillTape);

        return tags;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    // ── App-resource registration (MC3074 workaround — mirrors InstrumentPicker) ─────────────

    /// <summary>
    /// Registers a single shared <see cref="StrategyDataRequirementConverter"/> instance in
    /// <see cref="Application"/> resources under <see cref="ConverterKey"/> so that
    /// <c>{StaticResource StrategyTagsConverter}</c> resolves everywhere in the app.
    /// Safe to call multiple times (idempotent). No-op at design-time / headless hosts.
    /// </summary>
    public static void EnsureConverterRegistered()
    {
        var app = Application.Current;
        if (app is null) return; // design-time / headless — no app-level dictionary to seed
        if (!app.Resources.Contains(ConverterKey))
            app.Resources[ConverterKey] = new StrategyDataRequirementConverter();
    }

    // ── Brush helper ──────────────────────────────────────────────────────────────────────────

    private static Brush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
