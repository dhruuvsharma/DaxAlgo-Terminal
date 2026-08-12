using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Strategies.Parameters;
using RichParameterKind = TradingTerminal.Core.Strategies.Parameters.ParameterKind;

namespace TradingTerminal.BacktestStudio;

/// <summary>One optional numeric optimization axis projected from a catalog parameter.</summary>
public sealed partial class AxisRowViewModel : ObservableObject
{
    public AxisRowViewModel(StrategyParameter descriptor)
    {
        Descriptor = descriptor;
        var defaultValue = descriptor.Kind == RichParameterKind.Boolean
            ? descriptor.Default is true ? 1d : 0d
            : Convert.ToDouble(descriptor.Default ?? 0d, CultureInfo.InvariantCulture);
        _min = descriptor.Kind == RichParameterKind.Boolean ? 0 : descriptor.Min ?? defaultValue;
        _max = descriptor.Kind == RichParameterKind.Boolean ? 1 : descriptor.Max ?? defaultValue * 2 + 1;
        _step = descriptor.Kind == RichParameterKind.Boolean ? 1 : descriptor.Step is > 0 ? descriptor.Step.Value : 1;
    }

    public StrategyParameter Descriptor { get; }
    public string Name => Descriptor.Key;
    public string Label => Descriptor.DisplayName;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max;
    [ObservableProperty] private double _step;

    public ParameterAxis ToAxis() => ParameterAxis.Range(Name, Min, Max, Step);
}
