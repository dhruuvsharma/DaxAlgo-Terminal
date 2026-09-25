using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class TradierLoginForm : UserControl
{
    public TradierLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is TradierLoginFormViewModel vm)
            TokenBox.Password = vm.Token;
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TradierLoginFormViewModel vm && sender is PasswordBox pb)
            vm.Token = pb.Password;
    }
}
