using System.Windows;
using System.Windows.Controls;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Code-behind is PasswordBox plumbing only (Password is not a bindable DP): the three Telegram
/// credentials are masked on screen, pre-populated from the VM on DataContext changes, and pushed
/// back as the user types. Same pattern as the broker login forms.
/// </summary>
public partial class ArchiveSettingsView : UserControl
{
    private bool _populating;

    public ArchiveSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not ArchiveSettingsViewModel vm) return;
        _populating = true;
        try
        {
            ApiIdBox.Password = vm.ApiId > 0 ? vm.ApiId.ToString() : string.Empty;
            ApiHashBox.Password = vm.ApiHash;
            PhoneNumberBox.Password = vm.PhoneNumber;
        }
        finally
        {
            _populating = false;
        }
    }

    private void ApiIdBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_populating || DataContext is not ArchiveSettingsViewModel vm) return;
        vm.ApiId = int.TryParse(ApiIdBox.Password.Trim(), out var id) ? id : 0;
    }

    private void ApiHashBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_populating || DataContext is not ArchiveSettingsViewModel vm) return;
        vm.ApiHash = ApiHashBox.Password;
    }

    private void PhoneNumberBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_populating || DataContext is not ArchiveSettingsViewModel vm) return;
        vm.PhoneNumber = PhoneNumberBox.Password;
    }
}
