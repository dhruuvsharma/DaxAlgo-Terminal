using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class AlpacaLoginForm : UserControl
{
    public AlpacaLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is AlpacaLoginFormViewModel vm)
            ApiSecretBox.Password = vm.ApiSecret ?? string.Empty;
    }

    private void ApiSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AlpacaLoginFormViewModel vm && sender is PasswordBox pb)
            vm.ApiSecret = pb.Password;
    }
}
