using MahApps.Metro.Controls;

namespace DaxNewStrategy;

/// <summary>
/// The strategy's live window. The host builds it from the <c>StrategyFactoryRegistration</c> and sets
/// its DataContext to <see cref="DaxNewStrategyViewModel"/>, so the code-behind stays empty — all state
/// and behaviour live in the view-model (strict MVVM). Grow the XAML with your strategy's own readouts;
/// keep logic out of here.
/// </summary>
public partial class DaxNewStrategyWindow : MetroWindow
{
    public DaxNewStrategyWindow() => InitializeComponent();
}
