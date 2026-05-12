using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.UI;

/// <summary>
/// AvalonDock's <c>LayoutItemContainerStyle</c> is applied to BOTH document items and
/// anchorable items (e.g. the Strategies / Logs panes), so a style typed on
/// <c>LayoutDocumentItem</c> alone throws "TargetType does not match" when AvalonDock
/// tries to apply it to a <c>LayoutAnchorableItem</c>. This selector returns the document
/// style only when the container is actually a document item; anchorables keep their
/// theme defaults.
///
/// Lives in TradingTerminal.UI to dodge the WPF same-project markup-compile-pass-1
/// limitation (App's MainWindow.xaml needs the type visible before App's own C# is
/// compiled). Matches on type name to avoid forcing UI to take an AvalonDock dep.
/// </summary>
public sealed class DockTabStyleSelector : StyleSelector
{
    public Style? DocumentStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        container.GetType().Name == "LayoutDocumentItem" ? DocumentStyle : null;
}
