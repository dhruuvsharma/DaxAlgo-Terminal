using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class KeyedCryptoLoginForm : UserControl
{
    public KeyedCryptoLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not KeyedCryptoLoginFormBase vm) return;

        // PasswordBox.Password is not bindable, so a saved key is pushed in by hand. Not for the PEM
        // venue: its secret is multi-line, lives in the PEM box, and seeding it into the hidden
        // PasswordBox would echo back through PasswordChanged with its line breaks gone.
        if (vm.UsesSharedSecret) SecretBox.Password = vm.ApiSecret;
        if (vm.UsesPassphrase) PassphraseBox.Password = vm.Passphrase;
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is KeyedCryptoLoginFormBase { UsesSharedSecret: true } vm && sender is PasswordBox pb)
            vm.ApiSecret = pb.Password;
    }

    private void PassphraseBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is KeyedCryptoLoginFormBase { UsesPassphrase: true } vm && sender is PasswordBox pb)
            vm.Passphrase = pb.Password;
    }
}
