using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Provider setup. Code-behind is PasswordBox plumbing and the close button only — <c>Password</c> is
/// deliberately not a dependency property (a bindable one would sit in the binding engine's memory), so
/// the established pattern in this shell is to push it into the view-model on change.
/// </summary>
public partial class AiProviderSettingsWindow : MetroWindow
{
    public AiProviderSettingsWindow(AiProviderSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        // The row from the BOX, not from Selected. Every card now carries its own PasswordBox, so a
        // single "whichever row is selected" target would put one card's key on another card.
        if (sender is PasswordBox { DataContext: AiProviderSetupRow row } box)
            row.KeyEntry = box.Password;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// Opens <see cref="AiProviderSettingsWindow"/> over whatever window is active.
///
/// <para>Modal on purpose: the composer refreshes its provider list when this returns, and a modeless
/// window would let the user save a key into a picker that has already been rebuilt without it.</para>
/// </summary>
public sealed class AiProviderSettingsLauncher(Func<AiProviderSettingsViewModel> viewModel)
    : IAiProviderSettingsLauncher
{
    public void Open()
    {
        var window = new AiProviderSettingsWindow(viewModel())
        {
            // Whatever the user was looking at when they pressed the button. Null is fine — WPF then
            // centres on screen rather than throwing.
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
        };

        window.ShowDialog();
    }
}
