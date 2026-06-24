using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Strategies;

/// <summary>
/// Generic Avalonia window for any <c>LiveSignalStrategyViewModelBase</c>: binds the common surface
/// (instrument picker, status, Continue/Start/Arm/Stop/Clear, live signals) shared by every strategy.
/// Strategy-specific parameter editors are added per strategy as they're ported (Ornstein-Uhlenbeck
/// has a bespoke window); this gets a strategy on-screen and operable on Linux without per-strategy XAML.
/// </summary>
public partial class GenericStrategyWindow : Window
{
    public GenericStrategyWindow() => InitializeComponent();
}
