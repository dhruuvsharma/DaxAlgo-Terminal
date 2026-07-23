# TradingTerminal.UI — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Shell/TradingTerminal.UI/Controls/BusyOverlay.xaml.cs
```cs
   16: public partial class BusyOverlay : UserControl
   18: public BusyOverlay() => InitializeComponent();
   20: public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
   25: public bool IsActive
   31: public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
   36: public string Title
   42: public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
   47: public string Message
```

## src/windows/Shell/TradingTerminal.UI/Controls/CodeEditor.cs
```cs
   16: public sealed class CodeEditor : TextEditor
   21: public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
   35: public CodeEditor()
   44: public string Code
```

## src/windows/Shell/TradingTerminal.UI/Controls/InjectedFormHost.cs
```cs
   19: public static class InjectedFormHost
   23: public static Func<object, UIElement?>? ViewFactory { get; set; }
   25: public static readonly DependencyProperty FormProperty = DependencyProperty.RegisterAttached(
   28: public static void SetForm(DependencyObject element, object value) => element.SetValue(FormProperty, value);
   29: public static object? GetForm(DependencyObject element) => element.GetValue(FormProperty);
```

## src/windows/Shell/TradingTerminal.UI/Controls/InstrumentPicker.xaml.cs
```cs
   21: public partial class InstrumentPicker : UserControl
   25: public const string TagsConverterKey = "InstrumentTagsConverter";
   27: public InstrumentPicker()
   62: public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
   66: public IEnumerable? ItemsSource
   72: public static readonly DependencyProperty SelectedInstrumentProperty = DependencyProperty.Register(
   76: public SignalInstrument? SelectedInstrument
   82: public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
   87: public string SearchText
   93: public static readonly DependencyProperty MaxDropDownHeightProperty = DependencyProperty.Register(
   96: public double MaxDropDownHeight
```

## src/windows/Shell/TradingTerminal.UI/Controls/ParamSlider.xaml.cs
```cs
   15: public partial class ParamSlider : UserControl
   17: public ParamSlider() => InitializeComponent();
   19: public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
   22: public string Label
   28: public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
   32: public double Value
   38: public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
   41: public double Minimum
   47: public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
   50: public double Maximum
   56: public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
   59: public double Step
   65: public static readonly DependencyProperty SliderWidthProperty = DependencyProperty.Register(
   68: public double SliderWidth
```

## src/windows/Shell/TradingTerminal.UI/Controls/ParamSpinner.xaml.cs
```cs
   17: public partial class ParamSpinner : UserControl
   19: public ParamSpinner() => InitializeComponent();
   21: public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
   25: public string Label
   31: public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
   35: public double Value
   41: public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
   44: public double Minimum
   50: public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
   53: public double Maximum
   59: public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
   63: public double Step
   69: public static readonly DependencyProperty StringFormatProperty = DependencyProperty.Register(
   73: public string StringFormat
   79: public static readonly DependencyProperty SpinnerWidthProperty = DependencyProperty.Register(
   82: public double SpinnerWidth
```

## src/windows/Shell/TradingTerminal.UI/Controls/SimulatedDataBanner.cs
```cs
   17: public sealed class SimulatedDataBanner : Border
   19: public SimulatedDataBanner()
   57: public static BannerHost WrapTop(UIElement content)
   70: public static void AttachTo(Window window)
   83: public sealed class BannerHost : DockPanel { }
```

## src/windows/Shell/TradingTerminal.UI/Controls/Spinner.xaml.cs
```cs
   19: public partial class Spinner : UserControl
   23: public Spinner()
   45: public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
   50: public double Diameter
   56: public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
   61: public double Thickness
   67: public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
   72: public Brush ArcBrush
   78: public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
   83: public Brush TrackBrush
```

## src/windows/Shell/TradingTerminal.UI/Controls/StrategyChromeBar.xaml.cs
```cs
   20: public partial class StrategyChromeBar : UserControl
   22: public StrategyChromeBar()
   27: public static readonly DependencyProperty ShowPresetsProperty = DependencyProperty.Register(
   30: public static readonly DependencyProperty ShowPauseProperty = DependencyProperty.Register(
   33: public static readonly DependencyProperty ShowBarsCsvProperty = DependencyProperty.Register(
   36: public static readonly DependencyProperty ShowSignalsCsvProperty = DependencyProperty.Register(
   39: public static readonly DependencyProperty SnapshotNameProperty = DependencyProperty.Register(
   42: public static readonly DependencyProperty HelpTitleProperty = DependencyProperty.Register(
   45: public static readonly DependencyProperty HelpContentProperty = DependencyProperty.Register(
   48: public static readonly DependencyProperty ExtraActionsProperty = DependencyProperty.Register(
   51: public bool ShowPresets
   57: public bool ShowPause
   63: public bool ShowBarsCsv
   69: public bool ShowSignalsCsv
   76: public string SnapshotName
   83: public string HelpTitle
   90: public object? HelpContent
   98: public object? ExtraActions
```

## src/windows/Shell/TradingTerminal.UI/Controls/StrategySetupHost.cs
```cs
   25: public partial class StrategySetupHost : UserControl
   27: public StrategySetupHost() => InitializeComponent();
   29: public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
   33: public string Title
   39: public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
   43: public string Subtitle
   49: public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
   53: public string Description
   59: public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
   65: public string Tags
   75: public static readonly DependencyProperty TagItemsProperty = TagItemsKey.DependencyProperty;
   79: public IReadOnlyList<string> TagItems => (IReadOnlyList<string>)GetValue(TagItemsProperty);
   89: public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
   93: public string Heading
   99: public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
  104: public object? Form
```

## src/windows/Shell/TradingTerminal.UI/Controls/ViewExport.cs
```cs
   15: public static class ViewExport
   19: public static string? SavePng(FrameworkElement element, string suggestedName)
```

## src/windows/Shell/TradingTerminal.UI/Converters/BarFractionToGridLengthConverter.cs
```cs
   12: public sealed class BarFractionToGridLengthConverter : IValueConverter
   14: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   23: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/Base64ToImageSourceConverter.cs
```cs
   13: public sealed class Base64ToImageSourceConverter : IValueConverter
   15: public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   34: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/BrokerKindConverters.cs
```cs
    8: public sealed class BrokerInitialsConverter : IValueConverter
   10: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   20: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
   25: public sealed class BrokerSubtitleConverter : IValueConverter
   27: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   37: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
```

## src/windows/Shell/TradingTerminal.UI/Converters/CorrelationToBrushConverter.cs
```cs
   14: public sealed class CorrelationToBrushConverter : IValueConverter
   24: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   56: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/HaloBrushConverter.cs
```cs
   15: public sealed class HaloBrushConverter : IValueConverter
   19: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   46: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/HexToSoftBrushConverter.cs
```cs
   17: public sealed class HexToSoftBrushConverter : IValueConverter
   21: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   43: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/InstrumentTagsConverter.cs
```cs
   15: public sealed class InstrumentTagsConverter : IValueConverter
   17: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   33: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/InverseBooleanConverter.cs
```cs
    6: public sealed class InverseBooleanConverter : IValueConverter
    8: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   11: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/InverseBooleanToVisibilityConverter.cs
```cs
    8: public sealed class InverseBooleanToVisibilityConverter : IValueConverter
   10: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   13: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/ReferenceEqualsConverter.cs
```cs
   10: public sealed class ReferenceEqualsConverter : IMultiValueConverter
   12: public object Convert(object?[]? values, Type targetType, object? parameter, CultureInfo culture)
   18: public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
```

## src/windows/Shell/TradingTerminal.UI/Converters/StrategyClassificationConverter.cs
```cs
   29: public sealed class StrategyClassificationConverter : IValueConverter
   35: public const string ConverterKey = "StrategyClassConverter";
   58: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   86: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
  127: public static void EnsureConverterRegistered()
```

## src/windows/Shell/TradingTerminal.UI/Converters/StrategyDataRequirementConverter.cs
```cs
   39: public sealed class StrategyDataRequirementConverter : IValueConverter
   46: public const string ConverterKey = "StrategyTagsConverter";
   65: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   86: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   97: public static void EnsureConverterRegistered()
```

## src/windows/Shell/TradingTerminal.UI/Converters/StringToBrushConverter.cs
```cs
   18: public sealed class StringToBrushConverter : IValueConverter
   22: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   41: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/StringToVisibilityConverter.cs
```cs
    8: public sealed class StringToVisibilityConverter : IValueConverter
   10: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   13: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/StripCodeFencesConverter.cs
```cs
   13: public sealed class StripCodeFencesConverter : IValueConverter
   17: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
   54: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Converters/UnsignedStrategyConverter.cs
```cs
   17: public sealed class UnsignedStrategyConverter : IMultiValueConverter
   19: public const string ConverterKey = "UnsignedStrategyConverter";
   25: public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
   32: public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
   37: public static void EnsureConverterRegistered()
```

## src/windows/Shell/TradingTerminal.UI/CrashGuard.cs
```cs
   27: public static class CrashGuard
   37: public static string ReportDirectory { get; } = Path.Combine(
   43: public static void Install(string appName, Action<string, string, string>? log = null)
```

## src/windows/Shell/TradingTerminal.UI/Diagnostics/PluginFaultWatchdog.cs
```cs
   18: public static class PluginFaultWatchdog
   24: public static IDisposable Attach(
  100: public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
```

## src/windows/Shell/TradingTerminal.UI/Diagnostics/StrategyWindowSmoke.cs
```cs
   23: public static class StrategyWindowSmoke
   28: public static async Task<int> RunAsync(
```

## src/windows/Shell/TradingTerminal.UI/InstrumentTag.cs
```cs
   11: public sealed record InstrumentTag(string Text, Brush Background, Brush Foreground);
```

## src/windows/Shell/TradingTerminal.UI/SimulatedDataState.cs
```cs
   15: public static class SimulatedDataState
   20: public static bool IsActive => _isActive;
   23: public static event EventHandler? Changed;
   26: public static void Set(bool active)
```

## src/windows/Shell/TradingTerminal.UI/Strategies/NullToVisibilityConverter.cs
```cs
   12: public sealed class NullToVisibilityConverter : IValueConverter
   14: public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
   19: public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
```

## src/windows/Shell/TradingTerminal.UI/Strategies/ParameterTemplateSelector.cs
```cs
   13: public sealed class ParameterTemplateSelector : DataTemplateSelector
   15: public DataTemplate? NumberTemplate { get; set; }
   16: public DataTemplate? BooleanTemplate { get; set; }
   17: public DataTemplate? ChoiceTemplate { get; set; }
   18: public DataTemplate? TextTemplate { get; set; }
   20: public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyCatalogItemViewModel.cs
```cs
   14: public sealed partial class StrategyCatalogItemViewModel : ViewModelBase
   16: public StrategyCatalogItemViewModel(ITradingStrategy strategy)
   19: public StrategyCatalogItemViewModel(ITradingStrategy strategy, StrategyPresentation presentation)
   29: public ITradingStrategy Strategy { get; }
   31: public string Id => Strategy.Id;
   39: public ObservableCollection<string> CustomTags { get; } = [];
   41: public bool HasFormula => !string.IsNullOrWhiteSpace(Formula);
   42: public bool HasCustomTags => CustomTags.Count > 0;
   47: public void Apply(StrategyPresentation presentation)
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyImageTile.xaml.cs
```cs
   20: public partial class StrategyImageTile : UserControl
   25: public static readonly DependencyProperty ImagePathProperty = DependencyProperty.Register(
   32: public static readonly DependencyProperty IsFallbackProperty = IsFallbackKey.DependencyProperty;
   37: public StrategyImageTile()
   44: public string? ImagePath
   52: public bool IsFallback
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyParameterEditorView.xaml.cs
```cs
   12: public partial class StrategyParameterEditorView : UserControl
   14: public StrategyParameterEditorView()
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentation.cs
```cs
   11: public sealed record StrategyPresentation(
   18: public static readonly StrategyPresentation Empty = new();
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditor.cs
```cs
   10: public static class StrategyPresentationEditor
   12: public static bool ShowDialog(Window? owner, StrategyCatalogItemViewModel item)
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditorView.xaml.cs
```cs
    7: public partial class StrategyPresentationEditorView : Window
    9: public StrategyPresentationEditorView() => InitializeComponent();
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditorViewModel.cs
```cs
   14: public sealed partial class StrategyPresentationEditorViewModel : ViewModelBase
   18: public StrategyPresentationEditorViewModel(StrategyCatalogItemViewModel item)
   31: public string StrategyId => _item.Id;
   32: public string DefaultName { get; }
   33: public string DefaultDescription { get; }
   41: public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);
   72: public StrategyPresentation Build()
```

## src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationStore.cs
```cs
   14: public static class StrategyPresentationStore
   24: public static StrategyPresentation Get(string strategyId) =>
   29: public static void Save(string strategyId, StrategyPresentation presentation)
   40: public static void Remove(string strategyId)
```

## src/windows/Shell/TradingTerminal.UI/StrategyChartHelpers.cs
```cs
   11: public static class StrategyChartHelpers
   13: public static readonly ScottPlot.Color BackgroundColor = ScottPlot.Color.FromHex("#1E1E1E");
   14: public static readonly ScottPlot.Color GridColor       = ScottPlot.Color.FromHex("#3F3F46");
   15: public static readonly ScottPlot.Color TextColor       = ScottPlot.Color.FromHex("#DCDCDC");
   16: public static readonly ScottPlot.Color AccentColor     = ScottPlot.Color.FromHex("#007ACC");
   17: public static readonly ScottPlot.Color BullishColor    = ScottPlot.Color.FromHex("#26A69A");
   18: public static readonly ScottPlot.Color BearishColor    = ScottPlot.Color.FromHex("#EF5350");
   19: public static readonly ScottPlot.Color WarningColor    = ScottPlot.Color.FromHex("#F1C40F");
   20: public static readonly ScottPlot.Color MutedColor      = ScottPlot.Color.FromHex("#9D9D9D");
   23: public static void ConfigureDarkPlot(WpfPlot host, bool dateTimeBottom = true)
```

## src/windows/Shell/TradingTerminal.UI/StrategyWindowBase.cs
```cs
   22: public abstract class StrategyWindowBase : MetroWindow
   27: protected StrategyWindowBase()
   35: protected abstract IEnumerable<WpfPlot> ChartHosts { get; }
   38: protected abstract void OnRedrawCharts(LiveSignalStrategyViewModelBase vm);
   54: protected override void OnSourceInitialized(EventArgs e)
   94: protected virtual void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) { }
  108: protected static void ApplyAxisControls(ScottPlot.Plot plot, LiveSignalStrategyViewModelBase vm, IReadOnlyList<Bar> bars)
```

## src/windows/Shell/TradingTerminal.UI/StrategyWindowPlacementStore.cs
```cs
    9: public sealed record StrategyWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
   21: public static class StrategyWindowPlacementStore
   32: public static StrategyWindowPlacement? Load(string strategyId)
   43: public static void Save(string strategyId, StrategyWindowPlacement placement)
   69: public static void Attach(Window window, string key)
```

## src/windows/Shell/TradingTerminal.UI/Theming/ThemeManager.cs
```cs
    9: public sealed record ThemeDefinition(string Id, string Name, string PaletteUri);
   21: public interface IThemeManager
   24:     IReadOnlyList<ThemeDefinition> Themes { get; }
   26:     string CurrentThemeId { get; }
   29:     string CurrentBaseThemeId { get; }
   32:     event EventHandler? ThemesChanged;
   35:     void Apply(string themeId);
   38:     void ApplySaved();
   44:     IReadOnlyList<ThemeToken> EnumerateTokens();
   47:     Color? ReadColor(string key);
   50:     LinearGradientBrush? ReadGradient(string key);
   54:     void SetColorOverride(string key, Color value);
   57:     void SetGradientOverride(string key, IReadOnlyList<Color> stops);
   63:     ThemeDefinition RegisterCustomTheme(CustomThemeFile file);
   66:     void ExportThemeFile(CustomThemeFile file, string path);
   69:     CustomThemeFile ImportThemeFile(string path);
   72:     bool TryGetCustomTheme(string id, out CustomThemeFile file);
   76: public sealed class ThemeManager : IThemeManager
  104: public event EventHandler? ThemesChanged;
  106: public IReadOnlyList<ThemeDefinition> Themes => _all;
  108: public string CurrentThemeId { get; private set; } = _builtins[0].Id;
  110: public string CurrentBaseThemeId =>
  113: public void ApplySaved()
  119: public void Apply(string themeId)
  175: public Color? ReadColor(string key)
  186: public LinearGradientBrush? ReadGradient(string key) =>
  189: public void SetColorOverride(string key, Color value)
  203: public void SetGradientOverride(string key, IReadOnlyList<Color> stops)
  235: public IReadOnlyList<ThemeToken> EnumerateTokens()
  334: public ThemeDefinition RegisterCustomTheme(CustomThemeFile file)
  351: public void ExportThemeFile(CustomThemeFile file, string path) =>
  354: public CustomThemeFile ImportThemeFile(string path) =>
  358: public bool TryGetCustomTheme(string id, out CustomThemeFile file) => _customs.TryGetValue(id, out file!);
```

## src/windows/Shell/TradingTerminal.UI/Theming/ThemeToken.cs
```cs
    7: public enum ThemeTokenKind
   30: public sealed record ThemeToken(
   45: public sealed class CustomThemeFile
   48: public string Name { get; set; } = "Custom";
   51: public string BaseThemeId { get; set; } = "daxalgo-dark";
   54: public Dictionary<string, string> Colors { get; set; } = new();
   57: public Dictionary<string, List<string>> Gradients { get; set; } = new();
```
