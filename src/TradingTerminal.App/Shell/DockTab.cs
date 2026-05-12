namespace TradingTerminal.App.Shell;

/// <summary>
/// Lightweight model for an open document tab. AvalonDock's <c>DocumentsSource</c>
/// auto-wraps each item into an internal <c>LayoutDocument</c>, so the binding target
/// must NOT itself be a <c>LayoutDocument</c> (otherwise the visible content renders
/// as <c>LayoutDocument.ToString()</c>).
///
/// The MainWindow XAML's <c>LayoutItemContainerStyle</c> maps these properties onto the
/// wrapper <c>LayoutDocument</c>, and <c>LayoutItemTemplate</c> renders <see cref="Content"/>
/// into the document pane.
/// </summary>
public sealed class DockTab
{
    public required string Title { get; init; }
    public required string ContentId { get; init; }
    public required object Content { get; init; }
    public bool CanClose { get; init; } = true;
}
