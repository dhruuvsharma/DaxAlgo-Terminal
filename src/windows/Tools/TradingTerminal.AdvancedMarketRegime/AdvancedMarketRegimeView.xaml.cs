using System.Windows;
using System.Windows.Controls;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.AdvancedMarketRegime;

public partial class AdvancedMarketRegimeView : UserControl
{
    public AdvancedMarketRegimeView()
    {
        InitializeComponent();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var symbol = (DataContext as AdvancedMarketRegimeViewModel)?.SelectedInstrument?.Contract.Symbol ?? "regime";
        ViewExport.SavePng(this, $"advanced-regime-{symbol}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }
}
