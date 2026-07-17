using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// The shared instrument dropdown used everywhere — every strategy window and the App tools
/// (charts / order book / footprint / regime / recorder / backtest). One editable ComboBox: the user
/// types an instrument name and the drop-down filters as they type; each row shows the clean symbol
/// plus coloured pills (broker · asset class · data types). The owning view-model still owns the data
/// and the filtering; this control just binds <see cref="ItemsSource"/> (the filtered list),
/// <see cref="SelectedInstrument"/>, and <see cref="SearchText"/> (the term to filter by).
///
/// <para>Before 2026-07-17 this was a separate search TextBox stacked above a read-only ComboBox.
/// Merging them is why <see cref="InstrumentPickerFilter.Visible{T}"/> now returns the full universe
/// for an empty term: with one control, clicking the arrow must show the list.</para>
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
        Combo.KeyUp += OnComboKeyUp;
    }

    /// <summary>Typing has to drop the list open — otherwise the filter runs against a list the user
    /// can't see and the control looks dead. View mechanics only: what to show is the view-model's
    /// call, via <see cref="SearchText"/>.</summary>
    private void OnComboKeyUp(object sender, KeyEventArgs e)
    {
        // Navigation/commit keys drive the ComboBox itself; only text entry should force it open.
        if (e.Key is Key.Enter or Key.Escape or Key.Tab or Key.Up or Key.Down
            or Key.LeftAlt or Key.RightAlt or Key.System)
            return;
        if (!Combo.IsDropDownOpen) Combo.IsDropDownOpen = true;
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

    public static readonly DependencyProperty MaxDropDownHeightProperty = DependencyProperty.Register(
        nameof(MaxDropDownHeight), typeof(double), typeof(InstrumentPicker), new PropertyMetadata(420.0));

    public double MaxDropDownHeight
    {
        get => (double)GetValue(MaxDropDownHeightProperty);
        set => SetValue(MaxDropDownHeightProperty, value);
    }
}
