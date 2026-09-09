namespace TradingTerminal.App.Authoring;

/// <summary>
/// Asks the user for reference pictures.
///
/// <para>A seam because this project is deliberately WPF-free — the same reason
/// <see cref="IAiProviderSettingsLauncher"/> is one. A shell supplies a file dialog; a headless host
/// supplies nothing, and the pane's attach button is simply absent rather than broken.</para>
/// </summary>
public interface IReferencePicker
{
    /// <summary>
    /// Returns the chosen image file paths, or empty when the user cancelled.
    ///
    /// <para>Paths rather than bytes: the caller owns the size and type limits, and a picker that read
    /// files would have to know about them too.</para>
    /// </summary>
    Task<IReadOnlyList<string>> PickImagesAsync();
}
