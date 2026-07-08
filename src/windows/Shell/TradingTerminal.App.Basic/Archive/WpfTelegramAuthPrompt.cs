using System.Windows;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

namespace TradingTerminal.App.Archive;

/// <summary>
/// WPF-side bridge for the Telegram MTProto login flow. WTelegramClient's <c>Config</c> callback
/// is synchronous and fires from the WTelegram thread; whenever it asks for a value we don't
/// have in options (verification code, 2FA password), this bridge marshals a modal dialog onto
/// the UI thread and blocks the callback's thread until the user submits a value.
/// </summary>
public sealed class WpfTelegramAuthPrompt : ITelegramAuthPrompt
{
    public Task<string?> PromptAsync(string key, CancellationToken ct)
    {
        var (header, help) = LabelFor(key);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = Application.Current;
        if (app is null) { tcs.SetResult(null); return tcs.Task; }

        app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var dlg = new TelegramPromptDialog(header, help)
                {
                    Owner = app.MainWindow ?? app.Windows.OfType<Window>().FirstOrDefault(),
                };
                var ok = dlg.ShowDialog();
                tcs.TrySetResult(ok == true ? dlg.InputValue : null);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static (string Header, string Help) LabelFor(string key) => key switch
    {
        "verification_code" => ("Enter the Telegram code",
            "Telegram just messaged a verification code to your phone or the Telegram app on another device. Type it below."),
        "password" => ("Two-factor password",
            "Your Telegram account has cloud password (2FA) enabled. Enter it to finish logging in."),
        "phone_number" => ("Phone number",
            "Telegram needs your phone in international format (e.g. +91…)."),
        "first_name" => ("First name", "Used only if this phone has never signed up to Telegram."),
        "last_name" => ("Last name", "Used only if this phone has never signed up to Telegram."),
        _ => ($"Telegram needs: {key}", "Enter the value Telegram is asking for."),
    };
}
