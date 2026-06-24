using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class IbLoginForm : UserControl
{
    public IbLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is IbLoginFormViewModel vm)
            PasswordBox.Password = vm.Password ?? string.Empty;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is IbLoginFormViewModel vm && sender is PasswordBox pb)
            vm.Password = pb.Password;
    }
}
