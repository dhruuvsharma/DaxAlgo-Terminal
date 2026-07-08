using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Drop-in toolbar cluster giving a strategy window the shared Surface-Lab chrome — preset
/// picker, display-pause toggle, bars / signals CSV export, PNG snapshot, and a contextual
/// help popup — in one XAML tag.
///
/// Binds against the host window's DataContext <b>by convention</b>: it expects the property /
/// command names <c>LiveSignalStrategyViewModelBase</c> exposes (<c>IsPaused</c>,
/// <c>PresetNames</c> / <c>PresetName</c> / <c>SelectedPreset</c>, <c>SavePresetCommand</c> /
/// <c>DeletePresetCommand</c>, <c>ExportBarsCsvCommand</c> / <c>ExportSignalsCsvCommand</c>).
/// Bespoke strategy VMs adopt the same names to light the buttons up; hide what a VM doesn't
/// implement via <see cref="ShowPresets"/> / <see cref="ShowPause"/> / <see cref="ShowBarsCsv"/> /
/// <see cref="ShowSignalsCsv"/>. The PNG snapshot needs no VM support at all — the control
/// captures its host window's content root itself.
/// </summary>
public partial class StrategyChromeBar : UserControl
{
    public StrategyChromeBar()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ShowPresetsProperty = DependencyProperty.Register(
        nameof(ShowPresets), typeof(bool), typeof(StrategyChromeBar), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowPauseProperty = DependencyProperty.Register(
        nameof(ShowPause), typeof(bool), typeof(StrategyChromeBar), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowBarsCsvProperty = DependencyProperty.Register(
        nameof(ShowBarsCsv), typeof(bool), typeof(StrategyChromeBar), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowSignalsCsvProperty = DependencyProperty.Register(
        nameof(ShowSignalsCsv), typeof(bool), typeof(StrategyChromeBar), new PropertyMetadata(true));

    public static readonly DependencyProperty SnapshotNameProperty = DependencyProperty.Register(
        nameof(SnapshotName), typeof(string), typeof(StrategyChromeBar), new PropertyMetadata("strategy"));

    public static readonly DependencyProperty HelpTitleProperty = DependencyProperty.Register(
        nameof(HelpTitle), typeof(string), typeof(StrategyChromeBar), new PropertyMetadata("READING THIS WINDOW"));

    public static readonly DependencyProperty HelpContentProperty = DependencyProperty.Register(
        nameof(HelpContent), typeof(object), typeof(StrategyChromeBar), new PropertyMetadata(null));

    public bool ShowPresets
    {
        get => (bool)GetValue(ShowPresetsProperty);
        set => SetValue(ShowPresetsProperty, value);
    }

    public bool ShowPause
    {
        get => (bool)GetValue(ShowPauseProperty);
        set => SetValue(ShowPauseProperty, value);
    }

    public bool ShowBarsCsv
    {
        get => (bool)GetValue(ShowBarsCsvProperty);
        set => SetValue(ShowBarsCsvProperty, value);
    }

    public bool ShowSignalsCsv
    {
        get => (bool)GetValue(ShowSignalsCsvProperty);
        set => SetValue(ShowSignalsCsvProperty, value);
    }

    /// <summary>File-name stem for the PNG snapshot (e.g. <c>sigma-ic-flow</c>).</summary>
    public string SnapshotName
    {
        get => (string)GetValue(SnapshotNameProperty);
        set => SetValue(SnapshotNameProperty, value);
    }

    /// <summary>Header line of the help popup.</summary>
    public string HelpTitle
    {
        get => (string)GetValue(HelpTitleProperty);
        set => SetValue(HelpTitleProperty, value);
    }

    /// <summary>Window-specific help body (any content — typically a wrapped TextBlock).</summary>
    public object? HelpContent
    {
        get => GetValue(HelpContentProperty);
        set => SetValue(HelpContentProperty, value);
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { Content: FrameworkElement root }) return;
        ViewExport.SavePng(root, $"{SnapshotName}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }
}
