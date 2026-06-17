using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// A full-surface "loading" curtain: a dimmed backdrop plus a centred card with a <see cref="Spinner"/>,
/// a bold <see cref="Title"/> ("Opening Charts…") and a <see cref="Message"/> sub-line describing what
/// data or work is in flight. Drop it as the last (top-most) child of a window's root grid (spanning
/// every row/column) and bind <see cref="IsActive"/> to a busy flag.
///
/// <para>While <see cref="IsActive"/> is true the backdrop is opaque to hit-testing, so it blocks
/// interaction with the half-built UI underneath. When false the whole control collapses and is fully
/// click-through.</para>
/// </summary>
public partial class BusyOverlay : UserControl
{
    public BusyOverlay() => InitializeComponent();

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BusyOverlay),
        new PropertyMetadata(false, OnIsActiveChanged));

    /// <summary>When true the curtain is shown and blocks input; when false it collapses.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(BusyOverlay),
        new PropertyMetadata("Loading…"));

    /// <summary>Primary line — what is being opened (e.g. "Opening Order Flow Cube…").</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(BusyOverlay),
        new PropertyMetadata(string.Empty));

    /// <summary>Secondary detail — what data/functionality is loading (e.g. "Fetching history…").</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    // Only intercept input while actually busy, so a collapsed curtain never steals clicks.
    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BusyOverlay)d).IsHitTestVisible = (bool)e.NewValue;
}
