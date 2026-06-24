using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Observable editor for a single <see cref="StrategyParameter"/>. Every typed property
/// reads from and writes straight through to the shared <see cref="StrategyParameters"/>
/// bag, so the bag is always the single source of truth — and its clamping/coercion is
/// reflected back in the UI the instant a control commits an out-of-range value.
///
/// The view picks which property to bind by <see cref="Kind"/> (see
/// <c>ParameterTemplateSelector</c>); all numeric kinds share <see cref="NumberValue"/>.
/// </summary>
public sealed class ParameterEditorItem : ObservableObject
{
    public ParameterEditorItem(StrategyParameters bag, StrategyParameter parameter)
    {
        _bag = bag;
        Parameter = parameter;
    }

    private readonly StrategyParameters _bag;

    public StrategyParameter Parameter { get; }

    public string Key => Parameter.Key;
    public string DisplayName => Parameter.DisplayName;
    public string? Description => Parameter.Description;
    public string? Group => Parameter.Group;
    public string? Unit => Parameter.Unit;
    public ParameterKind Kind => Parameter.Kind;

    public bool HasRange => Parameter.Min.HasValue && Parameter.Max.HasValue;
    public double Min => Parameter.Min ?? double.MinValue;
    public double Max => Parameter.Max ?? double.MaxValue;
    public double Step => Parameter.Step ?? (Kind == ParameterKind.Integer ? 1 : 0.1);
    public bool IsInteger => Kind == ParameterKind.Integer;
    public IReadOnlyList<string> Choices => Parameter.Choices ?? Array.Empty<string>();

    /// <summary>Numeric value for both <see cref="ParameterKind.Integer"/> and <see cref="ParameterKind.Number"/>.</summary>
    public double NumberValue
    {
        get => _bag.GetDouble(Key);
        set
        {
            _bag.Set(Key, value);
            OnPropertyChanged();          // re-reads the (possibly clamped) value
            OnPropertyChanged(nameof(DisplayValue));
        }
    }

    public bool BoolValue
    {
        get => _bag.GetBool(Key);
        set { _bag.Set(Key, value); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayValue)); }
    }

    public string TextValue
    {
        get => _bag.GetString(Key);
        set { _bag.Set(Key, value); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayValue)); }
    }

    /// <summary>Selected item for <see cref="ParameterKind.Choice"/> (shares storage with text).</summary>
    public string SelectedChoice
    {
        get => _bag.GetString(Key);
        set { _bag.Set(Key, value); OnPropertyChanged(); OnPropertyChanged(nameof(DisplayValue)); }
    }

    /// <summary>Compact read-only rendering (with unit) for summaries and tooltips.</summary>
    public string DisplayValue => Kind switch
    {
        ParameterKind.Integer => $"{_bag.GetLong(Key)}{UnitSuffix}",
        ParameterKind.Number => $"{_bag.GetDouble(Key):0.###}{UnitSuffix}",
        ParameterKind.Boolean => _bag.GetBool(Key) ? "On" : "Off",
        _ => _bag.GetString(Key),
    };

    private string UnitSuffix => string.IsNullOrEmpty(Unit) ? "" : $" {Unit}";

    /// <summary>Re-reads every bound surface (after a bulk reset to defaults).</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(NumberValue));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(DisplayValue));
    }
}
