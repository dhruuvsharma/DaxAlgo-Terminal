using MahApps.Metro.Controls;

namespace TradingTerminal.Correlation;

/// <summary>
/// Standalone window hosting the Live Correlation Matrix tool. Pure view — all behaviour lives in
/// <see cref="LiveCorrelationMatrixViewModel"/>, assigned as the DataContext by the opener.
/// </summary>
public partial class LiveCorrelationMatrixWindow : MetroWindow
{
    public LiveCorrelationMatrixWindow()
    {
        InitializeComponent();
    }
}
