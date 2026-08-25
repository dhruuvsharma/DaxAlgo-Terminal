namespace TradingTerminal.App.Authoring;

/// <summary>
/// Opens the provider setup window.
///
/// <para>A seam because the view-model cannot reference a shell: the window is WPF and each edition owns
/// its own copy of the shell, while this view-model is written once and promoted to all of them. The same
/// arrangement <c>ICliWorkspaceLauncher</c> uses for the same reason.</para>
///
/// <para>Optional everywhere it is consumed. An edition that registers none simply has no button — not a
/// composer that throws when the user presses one.</para>
/// </summary>
public interface IAiProviderSettingsLauncher
{
    /// <summary>Shows the setup window, modal to the composer. Returns once it closes, so the caller can
    /// refresh a provider list that setup may have changed.</summary>
    void Open();
}
