using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>One row in the optimization axis editor: a parameter the user can turn into a sweep axis
/// with its own min/max/step. Defaults seed from the parameter descriptor's domain.</summary>
public sealed partial class AxisRowViewModel : ObservableObject
{
    public AxisRowViewModel(ParameterDescriptor descriptor)
    {
        Descriptor = descriptor;
        _min = double.IsFinite(descriptor.Min) ? descriptor.Min : descriptor.Default;
        _max = double.IsFinite(descriptor.Max) ? descriptor.Max : descriptor.Default * 2 + 1;
        _step = descriptor.Step > 0 ? descriptor.Step : 1;
    }

    public ParameterDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;
    public string Label => Descriptor.Label;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max;
    [ObservableProperty] private double _step;

    public ParameterAxis ToAxis() => ParameterAxis.Range(Name, Min, Max, Step);
}
