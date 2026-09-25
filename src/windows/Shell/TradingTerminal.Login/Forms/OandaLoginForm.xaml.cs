using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class OandaLoginForm : UserControl
{
    public OandaLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is OandaLoginFormViewModel vm)
            TokenBox.Password = vm.Token;
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is OandaLoginFormViewModel vm && sender is PasswordBox pb)
            vm.Token = pb.Password;
    }
}
