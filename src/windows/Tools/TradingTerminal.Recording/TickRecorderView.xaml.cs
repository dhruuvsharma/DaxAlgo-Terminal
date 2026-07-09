using System.Windows;
using System.Windows.Controls;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.Recording;

public partial class TickRecorderView : UserControl
{
    public TickRecorderView() { InitializeComponent(); }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        var symbol = (DataContext as TickRecorderViewModel)?.SelectedInstrument?.Contract.Symbol ?? "recorder";
        ViewExport.SavePng(this, $"recorder-{symbol}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }
}
