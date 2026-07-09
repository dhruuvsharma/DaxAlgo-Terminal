using System.Windows;
using TradingTerminal.UI.Controls;
using MahApps.Metro.Controls;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// View for the QuantConnect / LEAN tool window. Pure view concerns only — the only behaviour here is
/// keeping the streamed engine log scrolled to the latest line. All logic lives in
/// <see cref="QuantConnectViewModel"/>.
/// </summary>
public partial class QuantConnectWindow : MetroWindow
{
    public QuantConnectWindow()
    {
        InitializeComponent();
        RunLogBox.TextChanged += (_, _) => RunLogBox.ScrollToEnd();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        ViewExport.SavePng(root, $"lean-{DateTime.Now:yyyyMMdd-HHmmss}");
    }
}
