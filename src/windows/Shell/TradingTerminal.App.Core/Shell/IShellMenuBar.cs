namespace TradingTerminal.App.Shell;

/// <summary>
/// Marker for the per-edition menu <c>UserControl</c> the shell hosts in its <c>MenuBar</c> region.
/// Each edition registers its own menu (the Professional shell registers the full File/View/Tools/…/AI
/// menu); <see cref="MainShellFactory"/> resolves it from DI and assigns it to
/// <c>MainWindowViewModel.MenuBar</c>, which the <c>MainWindow</c> renders in a <c>ContentControl</c>.
/// The menu inherits the shell VM as its <c>DataContext</c> from the visual tree, so common items bind
/// to the VM's commands and tier-exclusive items bind through <c>ExtendedTools</c>.
/// </summary>
public interface IShellMenuBar
{
}
