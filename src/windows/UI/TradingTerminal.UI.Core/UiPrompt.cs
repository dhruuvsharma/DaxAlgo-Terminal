namespace TradingTerminal.UI;

/// <summary>
/// Portable confirmation-prompt seam — the sibling of <see cref="UiFile"/> and <see cref="UiThread"/>.
/// View-models ask for a typed confirmation through this instead of constructing a platform dialog,
/// so the view-model layer stays WPF-free and testable. The desktop shell wires it during start-up.
///
/// <para>The default returns <c>null</c>, meaning "cancelled". That is the right default twice over:
/// it is correct for headless callers and tests, and for a prompt whose whole purpose is to gate a
/// dangerous action, an unwired host must refuse rather than silently proceed.</para>
/// </summary>
public static class UiPrompt
{
    /// <summary>
    /// Asks the user to type something, returning exactly what they typed, or <c>null</c> if they
    /// cancelled or no UI host is wired. Callers compare the result themselves — this seam neither
    /// trims nor normalises, because the acknowledgements it gates are matched exactly.
    /// </summary>
    public static Func<string, string, string?> AskForText { get; set; }
        = static (_, _) => null;

    /// <summary>Shows <paramref name="message"/> under <paramref name="title"/> and returns the typed text.</summary>
    public static string? Ask(string title, string message) => AskForText(title, message);
}
