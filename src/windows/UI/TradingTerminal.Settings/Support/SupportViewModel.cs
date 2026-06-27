using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.UI;

namespace TradingTerminal.App.Support;

/// <summary>
/// View-model behind the "Support the developer" window. Two jobs: say thank-you, and let the user
/// write a note that reaches the developer. Delivery is intentionally low-tech — we hand a
/// pre-filled <c>mailto:</c> to the OS so the user's own mail client sends it (no SMTP credential,
/// no backend, nothing to leak in a public repo).
///
/// <para>The "support / donate" call-to-action is informational for v1 — a WinRAR-style optional
/// paywall is planned, but every feature stays free regardless. The hook is here so wiring a real
/// payment link later is a one-line change.</para>
/// </summary>
public sealed partial class SupportViewModel : ViewModelBase
{
    private readonly ILogger<SupportViewModel> _logger;

    public SupportViewModel(ILogger<SupportViewModel> logger)
    {
        _logger = logger;
    }

    public string ProductName => SupportInfo.ProductName;

    public string Version => SupportInfo.DisplayVersion;

    public string DeveloperEmail => SupportInfo.DeveloperEmail;

    public string ThankYouMessage =>
        $"Thank you for using {SupportInfo.ProductName}.\n\n" +
        "This is a solo-built, open-source project — every strategy, broker, and tool is free, " +
        "and always will be. If it's useful to you, a quick note below makes my day.";

    public string DonateMessage =>
        "Donations aren't open yet. When they are, supporting will be entirely optional — " +
        "pay if you can and want to, and if not, keep using everything for free. WinRAR-style.";

    /// <summary>The note the user types to the developer.</summary>
    [ObservableProperty]
    private string _feedbackText = string.Empty;

    /// <summary>Transient confirmation / error line shown under the editor.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Raised when the user is done — the window subscribes and closes itself, keeping the
    /// VM free of any reference to the View.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void SendFeedback()
    {
        var body = string.IsNullOrWhiteSpace(FeedbackText)
            ? string.Empty
            : FeedbackText.Trim();

        var subject = $"{SupportInfo.ProductName} {SupportInfo.DisplayVersion} — feedback";
        var uri =
            $"mailto:{SupportInfo.DeveloperEmail}" +
            $"?subject={Uri.EscapeDataString(subject)}" +
            $"&body={Uri.EscapeDataString(body)}";

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            StatusMessage = $"Opening your mail client to {SupportInfo.DeveloperEmail} — thank you!";
            _logger.LogInformation("Support feedback handed to the default mail client.");
        }
        catch (Exception ex)
        {
            // No default mail client (common on locked-down trading boxes). Don't lose the note —
            // tell the user where to send it manually.
            StatusMessage =
                $"Couldn't open a mail client. Please email your note to {SupportInfo.DeveloperEmail}.";
            _logger.LogWarning(ex, "Failed to launch mailto: handler for support feedback.");
        }
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SupportInfo.GitHubUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to open the project GitHub page.");
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
