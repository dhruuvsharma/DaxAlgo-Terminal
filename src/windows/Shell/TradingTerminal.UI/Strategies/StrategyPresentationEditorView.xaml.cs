using System.Windows;

namespace TradingTerminal.UI.Strategies;

/// <summary>Modal editor for a strategy card's presentation overrides. The only code-behind is closing
/// the dialog with a positive result on Save — everything else is in the view-model.</summary>
public partial class StrategyPresentationEditorView : Window
{
    public StrategyPresentationEditorView() => InitializeComponent();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
