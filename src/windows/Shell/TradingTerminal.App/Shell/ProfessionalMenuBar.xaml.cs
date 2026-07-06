using System.Windows.Controls;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Professional-edition menu bar. Registered as <see cref="IShellMenuBar"/> and hosted by the shell's
/// <c>MenuBar</c> region; it inherits the <c>MainWindowViewModel</c> as its <c>DataContext</c> from the
/// visual tree, so common menu items bind to the VM's commands and tier-exclusive items bind through
/// <c>ExtendedTools</c>. No code-behind logic (strict MVVM) — this is a pure view.
/// </summary>
public partial class ProfessionalMenuBar : UserControl, IShellMenuBar
{
    public ProfessionalMenuBar() => InitializeComponent();
}
