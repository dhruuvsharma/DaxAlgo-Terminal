using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Compact labeled slider with a live value readout, used for continuous tunables and chart
/// axis controls (scale / range / height exaggeration) in a strategy's top "param strip". The
/// label and current value sit on one row above the track so the control reads at a glance and
/// tiles in a horizontal toolbar.
///
/// <see cref="Value"/> binds two-way as a <see cref="double"/>; WPF coerces to/from int view-model
/// properties. Set <see cref="SnapToTick"/> for integer-stepped sliders.
/// </summary>
public partial class ParamSlider : UserControl
{
    public ParamSlider() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ParamSlider), new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ParamSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ParamSlider), new PropertyMetadata(0.0));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ParamSlider), new PropertyMetadata(100.0));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ParamSlider), new PropertyMetadata(1.0));

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public static readonly DependencyProperty SliderWidthProperty = DependencyProperty.Register(
        nameof(SliderWidth), typeof(double), typeof(ParamSlider), new PropertyMetadata(120.0));

    public double SliderWidth
    {
        get => (double)GetValue(SliderWidthProperty);
        set => SetValue(SliderWidthProperty, value);
    }
}
