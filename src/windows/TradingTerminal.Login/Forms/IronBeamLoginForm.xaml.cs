using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Login.Forms;

public partial class IronBeamLoginForm : UserControl
{
    public IronBeamLoginForm()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is IronBeamLoginFormViewModel vm)
            ApiKeyBox.Password = vm.ApiKey ?? string.Empty;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is IronBeamLoginFormViewModel vm && sender is PasswordBox pb)
            vm.ApiKey = pb.Password;
    }
}
