using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// A lightweight, size-agnostic loading spinner: a faint full ring with a brighter ~70% arc that
/// rotates continuously while the control is visible. Pure XAML/animation — no images. Used both
/// stand-alone (a small inline "this item is loading" glyph) and inside <see cref="BusyOverlay"/>.
///
/// <para>The arc length is derived from the circle's circumference in <see cref="StrokeThickness"/>
/// units so it looks identical at any <see cref="Diameter"/>. The rotation storyboard runs only
/// between <see cref="FrameworkElement.Loaded"/> and <see cref="FrameworkElement.Unloaded"/> so a
/// hidden/closed spinner never keeps the render thread busy.</para>
/// </summary>
public partial class Spinner : UserControl
{
    private readonly Storyboard _spin;

    public Spinner()
    {
        InitializeComponent();

        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(anim, Rot);
        Storyboard.SetTargetProperty(anim, new PropertyPath(RotateTransform.AngleProperty));
        _spin = new Storyboard();
        _spin.Children.Add(anim);

        Loaded += (_, _) => { UpdateArc(); _spin.Begin(); };
        Unloaded += (_, _) => _spin.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _spin.Begin();
            else _spin.Stop();
        };
    }

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(Spinner),
        new PropertyMetadata(28.0, (d, _) => ((Spinner)d).UpdateArc()));

    /// <summary>Outer width/height of the spinner, in DIPs.</summary>
    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Spinner),
        new PropertyMetadata(3.0, (d, _) => ((Spinner)d).UpdateArc()));

    /// <summary>Stroke width of both the track and the sweeping arc.</summary>
    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(Spinner),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x00))));

    /// <summary>Colour of the moving arc (defaults to the terminal amber accent).</summary>
    public Brush ArcBrush
    {
        get => (Brush)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(Spinner),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))));

    /// <summary>Colour of the faint static ring behind the arc.</summary>
    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>Recomputes the dash pattern so the arc covers ~70% of the ring regardless of size.</summary>
    private void UpdateArc()
    {
        if (Arc is null || Thickness <= 0) return;
        var circumference = Math.PI * Diameter;            // in DIPs
        var units = circumference / Thickness;             // StrokeDashArray is in stroke-thickness units
        var on = units * 0.7;
        var gap = units - on;
        Arc.StrokeDashArray = new DoubleCollection(new[] { on, gap });
    }
}
