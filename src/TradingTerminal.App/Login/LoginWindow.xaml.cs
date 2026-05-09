using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace TradingTerminal.App.Login;

public partial class LoginWindow : MetroWindow
{
    public LoginWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LoginViewModel vm)
        {
            // Restore the remembered IB password into its PasswordBox (one-way; PasswordBox.Password
            // can't be data-bound for security reasons, so we wire it manually).
            PasswordBox.Password = vm.Password ?? string.Empty;
            // Same for the cTrader OAuth secrets.
            CTraderClientSecretBox.Password = vm.CTraderClientSecret ?? string.Empty;
            CTraderAccessTokenBox.Password = vm.CTraderAccessToken ?? string.Empty;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focus the password box if a username is already remembered, otherwise the username box.
        if (DataContext is LoginViewModel vm && !string.IsNullOrWhiteSpace(vm.Username))
            PasswordBox.Focus();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && sender is PasswordBox pb)
            vm.Password = pb.Password;
    }

    private void CTraderClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && sender is PasswordBox pb)
            vm.CTraderClientSecret = pb.Password;
    }

    private void CTraderAccessTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && sender is PasswordBox pb)
            vm.CTraderAccessToken = pb.Password;
    }
}
