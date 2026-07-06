using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.App.Archive;

public partial class TelegramPromptDialog : MetroWindow
{
    public TelegramPromptDialog(string headerText, string helpText)
    {
        InitializeComponent();
        DataContext = new TelegramPromptDialogContext(headerText, helpText);
        Loaded += (_, _) => InputBox.Focus();
    }

    public string? InputValue => ((TelegramPromptDialogContext)DataContext).InputValue;

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        // Guard: empty input would propagate down to WTelegram's ConfigCallback and surface as
        // "value cannot be an empty string (Parameter: …)". Bounce focus back to the field instead.
        var ctx = (TelegramPromptDialogContext)DataContext;
        if (string.IsNullOrWhiteSpace(ctx.InputValue))
        {
            InputBox.Focus();
            return;
        }
        DialogResult = true;
        Close();
    }
}

internal sealed class TelegramPromptDialogContext
{
    public TelegramPromptDialogContext(string header, string help)
    {
        HeaderText = header;
        HelpText = help;
    }

    public string HeaderText { get; }
    public string HelpText { get; }
    public string InputValue { get; set; } = string.Empty;
}
