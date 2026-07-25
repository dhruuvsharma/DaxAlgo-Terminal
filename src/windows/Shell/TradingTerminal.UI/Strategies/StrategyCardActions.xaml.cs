using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.UI.Strategies;

/// <summary>Compact tag flyout and safe external-link action shared by all strategy-card editions.</summary>
public partial class StrategyCardActions : UserControl
{
    public StrategyCardActions()
    {
        StrategyDataRequirementConverter.EnsureConverterRegistered();
        StrategyClassificationConverter.EnsureConverterRegistered();
        InitializeComponent();
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StrategyCatalogItemViewModel { LinkUri: { } uri }) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Opening a missing or policy-blocked browser is best-effort and must not crash the shell.
        }
    }
}
