using Avalonia.Controls;

namespace TradingTerminal.UI.Avalonia;

/// <summary>
/// Generic Avalonia window for any <c>LiveSignalStrategyViewModelBase</c>: binds the common surface
/// (instrument picker, status, Continue/Start/Arm/Stop/Clear, live signals, last bid/ask) shared by
/// every live-signal strategy. Shipped here in the shared Avalonia UI lib so each strategy project's
/// StrategyFactoryRegistration can return one without duplicating XAML. Strategy-specific parameter
/// editors live in bespoke per-strategy windows (e.g. Ornstein-Uhlenbeck) where needed.
/// </summary>
public partial class GenericStrategyWindow : Window
{
    public GenericStrategyWindow() => InitializeComponent();
}
