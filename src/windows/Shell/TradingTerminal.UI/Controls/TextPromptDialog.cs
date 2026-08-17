using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahApps.Metro.Controls;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Small modal that asks the user to type an exact confirmation, backing the <see cref="UiPrompt"/>
/// seam. Built in code rather than XAML so it needs no resource dictionary of its own and any shell
/// can wire it in one line.
///
/// <para>Deliberately has no default-accept button and starts with OK disabled: it exists to gate
/// dangerous actions, so pressing Enter on an empty box must do nothing rather than confirm.</para>
/// </summary>
public sealed class TextPromptDialog : MetroWindow
{
    private readonly TextBox _input;

    public TextPromptDialog(string title, string message)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowMaxRestoreButton = false;
        ShowMinButton = false;

        _input = new TextBox { Margin = new Thickness(0, 0, 0, 14) };
        var ok = new Button { Content = "Confirm", MinWidth = 96, Padding = new Thickness(0, 6, 0, 6), IsEnabled = false };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 96,
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };

        _input.TextChanged += (_, _) => ok.IsEnabled = _input.Text.Length > 0;
        ok.Click += (_, _) => { DialogResult = true; Close(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        body.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
        body.Children.Add(_input);
        body.Children.Add(buttons);

        Content = body;
        Loaded += (_, _) => _input.Focus();

        // Enter confirms only once something has been typed; Escape always cancels.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ok.IsEnabled) { DialogResult = true; Close(); }
        };
    }

    /// <summary>Exactly what the user typed. Not trimmed — the caller matches it exactly.</summary>
    public string EnteredText => _input.Text;
}
