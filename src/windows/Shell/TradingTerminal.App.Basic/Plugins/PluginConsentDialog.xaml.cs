using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.App.Plugins;

/// <summary>
/// The consent dialog for a plugin the host cannot vouch for — unsigned, not pinned by our build, and
/// not from a pinned publisher. It states plainly what an in-process plugin can do (everything this
/// process can), names the publisher and file, and lists the capabilities the IL scan found, so the
/// user's "yes" is an informed one. Default button is "Don't run it".
/// </summary>
public partial class PluginConsentDialog : MetroWindow
{
    private PluginConsentDialog(PluginConsentRequest request)
    {
        InitializeComponent();

        Headline = $"Run “{request.DisplayName}”?";
        PublisherText = string.IsNullOrWhiteSpace(request.Publisher)
            ? "Publisher: unknown — this plugin is not signed and was not shipped with the app."
            : $"Publisher: {request.Publisher} (declared, not verified — the plugin is unsigned).";
        PathText = request.AssemblyPath;
        // The hash the decision is bound to: if the file changes, we ask again.
        HashText = $"sha256 {Shorten(request.Sha256)}";
        Capabilities = Describe(request.Scan);

        DataContext = this;
    }

    public string Headline { get; }
    public string PublisherText { get; }
    public string PathText { get; }
    public string HashText { get; }
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>Shows the dialog modally and returns the user's decision. Static so the caller never
    /// holds a half-answered dialog.</summary>
    public static bool Ask(PluginConsentRequest request)
    {
        var dialog = new PluginConsentDialog(request)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true;
    }

    private static IReadOnlyList<string> Describe(PluginScanReport scan)
    {
        var findings = scan.Findings
            .Where(f => f.Severity != PluginScanSeverity.Clean)
            .Select(f => "• " + f.Detail)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Nothing flagged is worth saying out loud — silence would read as "the scan found nothing to
        // report", which is not the same as "there is nothing to worry about".
        return findings.Count > 0
            ? findings
            : ["• Nothing beyond ordinary strategy code was flagged. That is not a guarantee: a static scan cannot see everything."];
    }

    private static string Shorten(string hash) =>
        hash.Length <= 16 ? hash : $"{hash[..8]}…{hash[^8..]}";

    private void OnConsentClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

/// <summary>Wires the dialog into the loader's <see cref="IPluginConsentPrompt"/> seam. Plugin loading
/// happens on the UI thread during composition, so the modal show is safe here.</summary>
public sealed class PluginConsentPrompt : IPluginConsentPrompt
{
    public bool RequestConsent(PluginConsentRequest request) => PluginConsentDialog.Ask(request);
}
