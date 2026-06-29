using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Shared "hero" chrome for a strategy's setup / parameter screen — the view shown before
/// <see cref="TradingTerminal.UI.LiveSignalStrategyViewModelBase.IsConfigured"/> flips. Renders a
/// centered card on a themed backdrop, split into two panes: a left branding pane
/// (<see cref="Title"/> / <see cref="Subtitle"/> / <see cref="Description"/> / <see cref="Tags"/> pills)
/// and a right pane that hosts the per-strategy form supplied as this control's content (<see cref="Form"/>).
///
/// <para>This replaces the old per-window "narrow card floating in a black window" look with one
/// consistent, friendlier layout. Each strategy window only supplies its own copy here (title binding,
/// a one-line description, optional capability tags, and the input fields); all the visual scaffolding
/// lives once in the control, so the look can be retuned in a single place for every strategy.</para>
///
/// <para>It's a <see cref="UserControl"/> (rather than a templated <see cref="ContentControl"/>) so the
/// layout lives in the control's own compiled XAML — WPF can't resolve a same-assembly type used in a
/// <c>TargetType</c>/<c>x:Type</c> inside a compiled <c>ResourceDictionary</c>, which a control template
/// would require. The injected form is exposed as the <see cref="Form"/> content property.</para>
/// </summary>
[ContentProperty(nameof(Form))]
public partial class StrategySetupHost : UserControl
{
    public StrategySetupHost() => InitializeComponent();

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StrategySetupHost), new PropertyMetadata(string.Empty));

    /// <summary>Strategy name shown large at the top of the branding pane (bind to <c>StrategyDisplayName</c>).</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(StrategySetupHost), new PropertyMetadata(string.Empty));

    /// <summary>Short caps-style kicker above the title (e.g. "MEAN REVERSION", "ORDER FLOW").</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(StrategySetupHost), new PropertyMetadata(string.Empty));

    /// <summary>One- or two-sentence explanation of what the strategy does, shown in the branding pane.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags), typeof(string), typeof(StrategySetupHost),
        new PropertyMetadata(string.Empty, OnTagsChanged));

    /// <summary>Optional capability/requirement pills as a comma-separated string (e.g.
    /// "L1, Bars, Depth, Trade tape"). Each entry renders as a chip; empty hides the row.</summary>
    public string Tags
    {
        get => (string)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    private static readonly DependencyPropertyKey TagItemsKey = DependencyProperty.RegisterReadOnly(
        nameof(TagItems), typeof(IReadOnlyList<string>), typeof(StrategySetupHost),
        new PropertyMetadata(Array.Empty<string>()));

    public static readonly DependencyProperty TagItemsProperty = TagItemsKey.DependencyProperty;

    /// <summary>The parsed <see cref="Tags"/> chips the template binds to. Read-only — set by splitting
    /// <see cref="Tags"/> on commas / semicolons / pipes.</summary>
    public IReadOnlyList<string> TagItems => (IReadOnlyList<string>)GetValue(TagItemsProperty);

    private static void OnTagsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var raw = e.NewValue as string ?? string.Empty;
        var items = raw.Split(new[] { ',', ';', '|' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        d.SetValue(TagItemsKey, items);
    }

    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(StrategySetupHost), new PropertyMetadata("Configure"));

    /// <summary>Small heading above the form in the right pane. Defaults to "Configure".</summary>
    public string Heading
    {
        get => (string)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
        nameof(Form), typeof(object), typeof(StrategySetupHost), new PropertyMetadata(null));

    /// <summary>The per-strategy input form rendered in the right pane. This is the control's content
    /// property, so it's set simply by nesting the form markup inside the <c>StrategySetupHost</c> tag.</summary>
    public object? Form
    {
        get => GetValue(FormProperty);
        set => SetValue(FormProperty, value);
    }
}
