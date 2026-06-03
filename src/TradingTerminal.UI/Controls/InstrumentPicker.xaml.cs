using System.Collections;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// The shared instrument dropdown used everywhere — every strategy window and the App tools
/// (regime / Markov / recorder). Bundles a search box + a ComboBox whose rows show the clean
/// symbol plus coloured pills (broker · asset class · data types). The owning view-model still
/// owns the data and filtering; this control just binds to <see cref="ItemsSource"/> (the
/// filtered list), <see cref="SelectedInstrument"/>, and <see cref="SearchText"/>.
/// </summary>
public partial class InstrumentPicker : UserControl
{
    /// <summary>Resource key under which the shared <see cref="InstrumentTagsConverter"/> is
    /// registered in <see cref="Application"/> resources (see <see cref="EnsureConverterRegistered"/>).</summary>
    public const string TagsConverterKey = "InstrumentTagsConverter";

    public InstrumentPicker()
    {
        EnsureConverterRegistered();
        InitializeComponent();
    }

    /// <summary>
    /// The row template references <see cref="InstrumentTagsConverter"/> via
    /// <c>{StaticResource InstrumentTagsConverter}</c> on a <c>Binding.Converter</c>. That can't be a
    /// <c>DynamicResource</c> (Converter isn't a DP) and can't be declared in this same-assembly XAML
    /// (MC3074), so we register a single shared instance in <see cref="Application"/> resources —
    /// the app-level fallback every StaticResource lookup ends at. Done in the ctor so it's present
    /// before the strategy window measures and realizes the template. Idempotent.
    /// </summary>
    private static void EnsureConverterRegistered()
    {
        var app = Application.Current;
        if (app is null) return; // design-time / headless host — no app-level dictionary to seed
        if (!app.Resources.Contains(TagsConverterKey))
            app.Resources[TagsConverterKey] = new InstrumentTagsConverter();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(InstrumentPicker), new PropertyMetadata(null));

    /// <summary>The (already filtered) instruments to show. Bind to the VM's <c>Instruments</c>.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedInstrumentProperty = DependencyProperty.Register(
        nameof(SelectedInstrument), typeof(SignalInstrument), typeof(InstrumentPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public SignalInstrument? SelectedInstrument
    {
        get => (SignalInstrument?)GetValue(SelectedInstrumentProperty);
        set => SetValue(SelectedInstrumentProperty, value);
    }

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(InstrumentPicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Two-way search text. Bind to the VM's <c>InstrumentSearchText</c>; the VM filters.</summary>
    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public static readonly DependencyProperty ShowSearchProperty = DependencyProperty.Register(
        nameof(ShowSearch), typeof(bool), typeof(InstrumentPicker), new PropertyMetadata(true));

    /// <summary>When false the integrated search box is hidden (e.g. a toolbar that has its own).</summary>
    public bool ShowSearch
    {
        get => (bool)GetValue(ShowSearchProperty);
        set => SetValue(ShowSearchProperty, value);
    }

    public static readonly DependencyProperty MaxDropDownHeightProperty = DependencyProperty.Register(
        nameof(MaxDropDownHeight), typeof(double), typeof(InstrumentPicker), new PropertyMetadata(420.0));

    public double MaxDropDownHeight
    {
        get => (double)GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }
}
