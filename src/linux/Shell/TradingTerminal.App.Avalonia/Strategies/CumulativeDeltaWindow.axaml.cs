using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Strategies;

/// <summary>
/// Bespoke Avalonia window for the cumulative-delta scalper. Its VM derives from ViewModelBase (a
/// distinct surface from LiveSignalStrategyViewModelBase), so it gets its own window rather than the
/// GenericStrategyWindow — binding the real, unmodified CumulativeDeltaViewModel.
/// </summary>
public partial class CumulativeDeltaWindow : Window
{
    public CumulativeDeltaWindow() => InitializeComponent();
}
