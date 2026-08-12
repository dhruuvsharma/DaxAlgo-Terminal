using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.BacktestStudio;

/// <summary>One typed editor row generated from the catalog's canonical rich parameter schema.</summary>
public sealed partial class ParamRowViewModel : ObservableObject
{
    public ParamRowViewModel(StrategyParameter descriptor)
    {
        Descriptor = descriptor;
        _value = descriptor.Default;
    }

    public StrategyParameter Descriptor { get; }
    public string Name => Descriptor.Key;
    public string Label => Descriptor.DisplayName;
    public ParameterKind Kind => Descriptor.Kind;
    public IReadOnlyList<string> Choices => Descriptor.Choices ?? [];
    public string? Description => Descriptor.Description;
    public bool IsBoolean => Kind == ParameterKind.Boolean;
    public bool IsChoice => Kind == ParameterKind.Choice;
    public bool IsText => Kind == ParameterKind.Text;
    public bool IsNumeric => Kind is ParameterKind.Integer or ParameterKind.Number;
    public string DefaultText => Convert.ToString(Descriptor.Default, CultureInfo.InvariantCulture) ?? string.Empty;
    public string RangeText => Descriptor.Min is null && Descriptor.Max is null
        ? "Unbounded"
        : $"{Format(Descriptor.Min, "-∞")} to {Format(Descriptor.Max, "+∞")}";

    [ObservableProperty] private object? _value;

    public object? Resolved => Value;

    private static string Format(double? value, string fallback) =>
        value?.ToString("G", CultureInfo.InvariantCulture) ?? fallback;
}
