using MahApps.Metro.Controls;

namespace TradingTerminal.App.Correlation;

/// <summary>
/// Standalone window hosting the Correlation Matrix tool. Pure view — all behaviour lives in
/// <see cref="CorrelationMatrixViewModel"/>, assigned as the DataContext by the opener.
/// </summary>
public partial class CorrelationMatrixWindow : MetroWindow
{
    public CorrelationMatrixWindow()
    {
        InitializeComponent();
    }
}
