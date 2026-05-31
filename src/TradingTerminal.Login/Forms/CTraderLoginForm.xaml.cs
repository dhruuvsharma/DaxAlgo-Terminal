using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class CTraderLoginForm : UserControl
{
    public CTraderLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is CTraderLoginFormViewModel vm)
        {
            ClientSecretBox.Password = vm.ClientSecret ?? string.Empty;
            AccessTokenBox.Password = vm.AccessToken ?? string.Empty;
        }
    }

    private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CTraderLoginFormViewModel vm && sender is PasswordBox pb)
            vm.ClientSecret = pb.Password;
    }

    private void AccessTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CTraderLoginFormViewModel vm && sender is PasswordBox pb)
            vm.AccessToken = pb.Password;
    }
}
