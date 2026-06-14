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

    // DESIGN-NOTE: we new the three login-form UserControls here rather than resolving
    // them from DI because they live in the same WPF project — MarkupCompilePass1 needs
    // their generated `InitializeComponent` types visible at compile time, and that
    // forces same-assembly construction. They have no injectable dependencies today (the
    // VMs do, and they're already DI-resolved on LoginViewModel). If any of these
    // UserControls ever need injected services, move them to a separate assembly and
    // register them in DI alongside their VMs.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not LoginViewModel vm) return;

        if (vm.IbForm is { } ib)
            IbFormHost.Content = new IbLoginForm { DataContext = ib };
        if (vm.NinjaForm is { } nt)
            NinjaFormHost.Content = new NinjaLoginForm { DataContext = nt };
        if (vm.CTraderForm is { } ct)
            CTraderFormHost.Content = new CTraderLoginForm { DataContext = ct };
        if (vm.AlpacaForm is { } al)
            AlpacaFormHost.Content = new AlpacaLoginForm { DataContext = al };
        if (vm.BinanceForm is { } bn)
            BinanceFormHost.Content = new BinanceLoginForm { DataContext = bn };
        if (vm.IronBeamForm is { } ib2)
            IronBeamFormHost.Content = new IronBeamLoginForm { DataContext = ib2 };
        if (vm.LondonStrategicEdgeForm is { } lse)
            LondonStrategicEdgeFormHost.Content = new LondonStrategicEdgeLoginForm { DataContext = lse };
        if (vm.UpstoxForm is { } up)
            UpstoxFormHost.Content = new UpstoxLoginForm { DataContext = up };
        if (vm.CoinbaseForm is { } cb)
            CoinbaseFormHost.Content = new CoinbaseLoginForm { DataContext = cb };
        if (vm.BybitForm is { } by)
            BybitFormHost.Content = new BybitLoginForm { DataContext = by };
        if (vm.KrakenForm is { } kr)
            KrakenFormHost.Content = new KrakenLoginForm { DataContext = kr };
        if (vm.OkxForm is { } ok)
            OkxFormHost.Content = new OkxLoginForm { DataContext = ok };
    }
}
