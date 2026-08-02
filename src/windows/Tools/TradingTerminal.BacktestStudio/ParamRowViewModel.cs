using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>One editable row in the parameter panel, generated from a kernel's
/// <see cref="ParameterDescriptor"/> so the Studio's tuning surface always matches the schema.</summary>
public sealed partial class ParamRowViewModel : ObservableObject
{
    public ParamRowViewModel(ParameterDescriptor descriptor)
    {
        Descriptor = descriptor;
        _value = descriptor.Default;
    }

    public ParameterDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;
    public string Label => Descriptor.Label;

    [ObservableProperty] private double _value;

    /// <summary>The value clamped into the descriptor's domain — what actually feeds the run.</summary>
    public double Resolved => Descriptor.Clamp(Value);
}
