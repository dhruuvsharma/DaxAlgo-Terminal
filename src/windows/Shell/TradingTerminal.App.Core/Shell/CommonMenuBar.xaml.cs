using System.Windows.Controls;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Common menu bar for the Basic + Intermediate editions. Registered as <see cref="IShellMenuBar"/>
/// and hosted by the shell's <c>MenuBar</c> region; it inherits the <c>MainWindowViewModel</c> as its
/// <c>DataContext</c> from the visual tree. Every item binds to a common VM command (no
/// <c>ExtendedTools</c>), so it carries none of the Professional-only tool menus. No code-behind logic
/// (strict MVVM) — this is a pure view.
/// </summary>
public partial class CommonMenuBar : UserControl, IShellMenuBar
{
    public CommonMenuBar() => InitializeComponent();
}
