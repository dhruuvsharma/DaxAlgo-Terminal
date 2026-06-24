using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.App.Login;

/// <summary>
/// Shell-only code-behind. Every broker row is projected through the single <c>DataTemplate</c> in
/// <c>LoginWindow.xaml</c>; that template's <see cref="BrokerFormHost"/> attached property hosts the
/// right credential UserControl per form view-model. The VM→view switch lives here (not in the
/// attached property) because it references same-project XAML-generated UserControl types, which only
/// the main compile pass resolves — see <see cref="InjectedFormHost"/> for the MarkupCompilePass1 note.
/// </summary>
public partial class LoginWindow : MetroWindow
{
    static LoginWindow() => InjectedFormHost.ViewFactory = BuildForm;

    public LoginWindow() => InitializeComponent();

    private static UIElement? BuildForm(object vm) => vm switch
    {
        IbLoginFormViewModel f => new IbLoginForm { DataContext = f },
        NinjaLoginFormViewModel f => new NinjaLoginForm { DataContext = f },
        CTraderLoginFormViewModel f => new CTraderLoginForm { DataContext = f },
        AlpacaLoginFormViewModel f => new AlpacaLoginForm { DataContext = f },
        BinanceLoginFormViewModel f => new BinanceLoginForm { DataContext = f },
        IronBeamLoginFormViewModel f => new IronBeamLoginForm { DataContext = f },
        LondonStrategicEdgeLoginFormViewModel f => new LondonStrategicEdgeLoginForm { DataContext = f },
        UpstoxLoginFormViewModel f => new UpstoxLoginForm { DataContext = f },
        CoinbaseLoginFormViewModel f => new CoinbaseLoginForm { DataContext = f },
        BybitLoginFormViewModel f => new BybitLoginForm { DataContext = f },
        KrakenLoginFormViewModel f => new KrakenLoginForm { DataContext = f },
        OkxLoginFormViewModel f => new OkxLoginForm { DataContext = f },
        _ => null,
    };
}
