using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Compact labeled numeric spinner (MahApps <c>NumericUpDown</c>) used to tile a strategy's
/// tunables into the always-visible top "param strip". A label sits above a right-aligned
/// spinner so several controls line up cleanly in a horizontal toolbar.
///
/// <see cref="Value"/> is a <see cref="double"/> and binds two-way; WPF coerces it to/from an
/// <c>int</c> or <c>long</c> view-model property automatically, so the same control drives both
/// integer and real-valued parameters (set <see cref="StringFormat"/> to "0" for integers).
/// The hosting strip sets <c>IsEnabled</c> on this control to lock engine-baked parameters
/// while the strategy is streaming.
/// </summary>
public partial class ParamSpinner : UserControl
{
    public ParamSpinner() => InitializeComponent();

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ParamSpinner), new PropertyMetadata(string.Empty));

    /// <summary>Caption shown above the spinner. Bake any unit suffix into the text (e.g. "Window (bars)").</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ParamSpinner),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ParamSpinner), new PropertyMetadata(0.0));

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ParamSpinner), new PropertyMetadata(100.0));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(ParamSpinner), new PropertyMetadata(1.0));

    /// <summary>Increment applied per click / arrow.</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public static readonly DependencyProperty StringFormatProperty = DependencyProperty.Register(
        nameof(StringFormat), typeof(string), typeof(ParamSpinner), new PropertyMetadata("0.###"));

    /// <summary>Display format; "0" for integers, "F2"/"0.00000" for reals.</summary>
    public string StringFormat
    {
        get => (string)GetValue(StringFormatProperty);
        set => SetValue(StringFormatProperty, value);
    }

    public static readonly DependencyProperty SpinnerWidthProperty = DependencyProperty.Register(
        nameof(SpinnerWidth), typeof(double), typeof(ParamSpinner), new PropertyMetadata(76.0));

    public double SpinnerWidth
    {
        get => (double)GetValue(SpinnerWidthProperty);
        set => SetValue(SpinnerWidthProperty, value);
    }
}
