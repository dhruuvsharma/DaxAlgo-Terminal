namespace TradingTerminal.UI;

/// <summary>
/// Portable file-picker seam — the WPF-free counterpart to <see cref="UiThread"/>. VMs call these
/// hooks instead of a platform dialog. The WPF shell wires them to
/// <c>Microsoft.Win32.OpenFileDialog/SaveFileDialog</c>; the default returns <c>null</c> (cancelled),
/// which is correct for headless callers and tests.
/// </summary>
public static class UiFile
{
    /// <summary>Show an open-file picker. <paramref name="description"/> labels the filter and
    /// <paramref name="extensions"/> are bare extensions (e.g. <c>"parquet"</c>). Returns the chosen
    /// path, or <c>null</c> if cancelled / no UI host is wired.</summary>
    public static Func<string, IReadOnlyList<string>, Task<string?>> OpenAsync { get; set; }
        = static (_, _) => Task.FromResult<string?>(null);

    /// <summary>Show a save-file picker with a suggested file name. Returns the chosen path or
    /// <c>null</c>.</summary>
    public static Func<string, IReadOnlyList<string>, string, Task<string?>> SaveAsync { get; set; }
        = static (_, _, _) => Task.FromResult<string?>(null);
}
