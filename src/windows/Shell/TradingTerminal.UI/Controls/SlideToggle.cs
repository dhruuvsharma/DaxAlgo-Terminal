using System.Windows;
using System.Windows.Media;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Attached properties for the shared sliding two-state switch (<c>App.SlideToggle</c>).
///
/// <para>The switch exists because some choices are too consequential for a checkbox: both
/// destinations are written on the track and the knob rests over the active one, so the current
/// state is readable at a glance rather than inferred from a tick. Paper/Real was the first; the
/// template is parameterised here rather than copied so the next one is four attached properties
/// instead of another 140 lines of <see cref="System.Windows.Controls.ControlTemplate"/>.</para>
///
/// <para>Accents are <see cref="Brush"/>es rather than colours on purpose: the knob crossfades
/// between two filled layers instead of animating a colour, which is what lets callers pass theme
/// resources that a <c>ColorAnimation</c> could not bind to.</para>
/// </summary>
public static class SlideToggle
{
    /// <summary>Text on the left half — the state when unchecked.</summary>
    public static readonly DependencyProperty LeftLabelProperty =
        DependencyProperty.RegisterAttached(
            "LeftLabel",
            typeof(string),
            typeof(SlideToggle),
            new PropertyMetadata(string.Empty));

    /// <summary>Text on the right half — the state when checked.</summary>
    public static readonly DependencyProperty RightLabelProperty =
        DependencyProperty.RegisterAttached(
            "RightLabel",
            typeof(string),
            typeof(SlideToggle),
            new PropertyMetadata(string.Empty));

    /// <summary>Knob fill while unchecked.</summary>
    public static readonly DependencyProperty LeftAccentProperty =
        DependencyProperty.RegisterAttached(
            "LeftAccent",
            typeof(Brush),
            typeof(SlideToggle),
            new PropertyMetadata(Brushes.SteelBlue));

    /// <summary>Knob fill while checked.</summary>
    public static readonly DependencyProperty RightAccentProperty =
        DependencyProperty.RegisterAttached(
            "RightAccent",
            typeof(Brush),
            typeof(SlideToggle),
            new PropertyMetadata(Brushes.IndianRed));

    public static string GetLeftLabel(DependencyObject element) =>
        (string)element.GetValue(LeftLabelProperty);

    public static void SetLeftLabel(DependencyObject element, string value) =>
        element.SetValue(LeftLabelProperty, value);

    public static string GetRightLabel(DependencyObject element) =>
        (string)element.GetValue(RightLabelProperty);

    public static void SetRightLabel(DependencyObject element, string value) =>
        element.SetValue(RightLabelProperty, value);

    public static Brush GetLeftAccent(DependencyObject element) =>
        (Brush)element.GetValue(LeftAccentProperty);

    public static void SetLeftAccent(DependencyObject element, Brush value) =>
        element.SetValue(LeftAccentProperty, value);

    public static Brush GetRightAccent(DependencyObject element) =>
        (Brush)element.GetValue(RightAccentProperty);

    public static void SetRightAccent(DependencyObject element, Brush value) =>
        element.SetValue(RightAccentProperty, value);
}
