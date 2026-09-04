using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Charts;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Workspace;

/// <summary>
/// What the shell is pointed at: one instrument, one timeframe.
///
/// <para>Owned by the shell and shared with whichever canvas is showing, which is the entire reason
/// the chrome is consistent. A canvas does not fetch its own instrument list and does not draw its
/// own timeframe buttons; it is told, and it follows.</para>
/// </summary>
public sealed partial class WorkspaceSubject : ObservableObject
{
    [ObservableProperty] private TradableInstrument? _instrument;
    [ObservableProperty] private ChartTimeframe? _timeframe;
}

/// <summary>
/// How a canvas answers to the shell's <see cref="WorkspaceSubject"/>.
///
/// <para><see cref="Follows"/> is the point of the exercise and the default. The other two exist
/// because pretending otherwise is what makes a header lie: a depth ladder is inherently about one
/// book, and a cross-sectional surface spans a whole universe at once. Both would render something
/// that has nothing to do with the symbol shown above them.</para>
/// </summary>
public enum CanvasSubjectMode
{
    /// <summary>Re-renders when the shell's selection changes.</summary>
    Follows,

    /// <summary>Carries its own, and the shell's control is disabled with the canvas's reason shown
    /// beside it — an honest "this canvas chose for you" rather than a control that silently does
    /// nothing.</summary>
    Pins,

    /// <summary>Has no use for it at all; the shell's control is hidden rather than disabled.</summary>
    Ignores,
}

/// <summary>
/// Everything a canvas is handed when the shell puts it on screen.
/// </summary>
/// <param name="Services">The composition root, so a canvas can resolve whatever it actually needs
/// without the shell having to know what that is.</param>
/// <param name="Subject">The live instrument/timeframe selection. A <see cref="CanvasSubjectMode.Follows"/>
/// canvas subscribes to its property changes.</param>
public sealed record WorkspaceCanvasContext(IServiceProvider Services, WorkspaceSubject Subject);

/// <summary>
/// What a canvas contributes to the shell besides its own picture: an optional options rail and a
/// status line. Both are the shell's furniture, filled by the canvas — the price chart's chart-type
/// and indicator checkboxes are its contribution, not the shell's.
/// </summary>
/// <param name="View">The picture itself, and the only required part.</param>
/// <param name="OptionsRail">Shown in the collapsible right-hand rail; null hides the rail.</param>
/// <param name="Status">Bound to the footer; null leaves the footer to the shell.</param>
public sealed record WorkspaceCanvasView(
    FrameworkElement View,
    FrameworkElement? OptionsRail = null,
    IObservable<string>? Status = null)
{
    /// <summary>Called when the canvas leaves the centre, so it can stop work it started.
    ///
    /// <para>Not decoration. The price chart owns a WebView2, whose out-of-process composition sits
    /// ABOVE any WPF content in the same cell — so leaving it realised behind another canvas paints it
    /// over the top of whatever replaced it. The shell disposes on every swap and the chart tears its
    /// browser down; see the swap comment in WorkspaceShell.</para>
    /// </summary>
    public IDisposable? Lifetime { get; init; }
}

/// <summary>
/// One thing the shell can put in its centre.
///
/// <para>A record rather than an interface because a canvas is a factory plus three facts, and the
/// registration seam wants values it can put in a list. Registering one is how a surface the shell has
/// never heard of — an Order Book, a 3D surface, a Professional-only lab — appears in its picker
/// without the base shell acquiring a reference to it.</para>
/// </summary>
/// <param name="Id">Stable, lowercase, used for persistence: "price-chart", "authored-unit".</param>
/// <param name="DisplayName">What the picker shows.</param>
/// <param name="Group">Groups the picker's rows: "Charts", "Order flow", "Authored".</param>
/// <param name="Create">Builds the view. Called once per activation, not once per registration.</param>
/// <param name="Instrument">Whether the shell's instrument reaches this canvas.</param>
/// <param name="Timeframe">Whether the shell's timeframe reaches this canvas.</param>
/// <param name="PinnedReason">Shown beside a disabled control when this canvas pins. Required when
/// either mode is <see cref="CanvasSubjectMode.Pins"/>, because "why is this greyed out" is the only
/// question a disabled control ever provokes.</param>
public sealed record WorkspaceCanvas(
    string Id,
    string DisplayName,
    string Group,
    Func<WorkspaceCanvasContext, WorkspaceCanvasView> Create,
    CanvasSubjectMode Instrument = CanvasSubjectMode.Follows,
    CanvasSubjectMode Timeframe = CanvasSubjectMode.Follows,
    string? PinnedReason = null)
{
    public override string ToString() => DisplayName;
}
