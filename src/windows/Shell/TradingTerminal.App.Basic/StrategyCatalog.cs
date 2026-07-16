using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App;

/// <summary>
/// Freezable that carries a DataContext across a boundary where WPF's normal inheritance doesn't
/// reach — here, the strategy catalog's <c>CollectionContainer</c> and the Vibe card's
/// <c>ContextMenu</c>, both of which sit outside the visual tree. Drop one in a FrameworkElement's
/// resources with <c>Data="{Binding}"</c> and reach the view-model through
/// <c>{Binding Data.Xxx, Source={StaticResource ...}}</c>.
/// </summary>
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));
}

/// <summary>
/// Marker appended to the strategy catalog so the trailing "Vibe Quant" call-to-action tile flows as
/// the last card in the same WrapPanel. Double-click opens Vibe Quant; right-click shows the
/// Vibe Quant · Launch CLI menu. It is not an <c>ITradingStrategy</c> and never becomes the ListBox
/// selection (see <c>MainWindow.StrategyItem_Selected</c>).
/// </summary>
public sealed class VibeCardItem { }

/// <summary>
/// Picks the strategy-card template for real catalog entries and the Vibe-Quant call-to-action
/// template for the trailing <see cref="VibeCardItem"/>.
/// </summary>
public sealed class StrategyCatalogTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StrategyTemplate { get; set; }
    public DataTemplate? VibeCardTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is VibeCardItem ? VibeCardTemplate : StrategyTemplate;
}
