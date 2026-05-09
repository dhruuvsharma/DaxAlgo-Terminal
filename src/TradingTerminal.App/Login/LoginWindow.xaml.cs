using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.App.Login.Forms;

namespace TradingTerminal.App.Login;

/// <summary>
/// Shell-only code-behind. The actual broker forms (UserControls in the same project)
/// are instantiated here and injected into the named <c>ContentControl</c> hosts in XAML.
/// We can't <c>&lt;forms:IbLoginForm /&gt;</c> directly in the markup because WPF's
/// MarkupCompilePass1 doesn't resolve same-project XAML-generated UserControl partial
/// classes during sibling-XAML compile.
/// </summary>
public partial class LoginWindow : MetroWindow
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not LoginViewModel vm) return;

        if (vm.IbForm is { } ib)
            IbFormHost.Content = new IbLoginForm { DataContext = ib };
        if (vm.NinjaForm is { } nt)
            NinjaFormHost.Content = new NinjaLoginForm { DataContext = nt };
        if (vm.CTraderForm is { } ct)
            CTraderFormHost.Content = new CTraderLoginForm { DataContext = ct };
    }
}
