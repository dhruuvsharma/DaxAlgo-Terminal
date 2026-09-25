using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class SessionLoginForm : UserControl
{
    public SessionLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // PasswordBox.Password is not bindable, so saved values are pushed in by hand.
        if (e.NewValue is not SessionBrokerLoginFormViewModel vm) return;
        SecretBox.Password = vm.ApiSecret;
        PassphraseBox.Password = vm.Passphrase;
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionBrokerLoginFormViewModel vm && sender is PasswordBox pb) vm.ApiSecret = pb.Password;
    }

    private void PassphraseBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SessionBrokerLoginFormViewModel vm && sender is PasswordBox pb) vm.Passphrase = pb.Password;
    }
}
