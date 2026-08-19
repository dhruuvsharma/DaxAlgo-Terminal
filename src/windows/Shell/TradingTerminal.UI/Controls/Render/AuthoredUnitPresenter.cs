using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DaxAlgo.Sdk;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>One line in the unit's activity log.</summary>
/// <param name="TimestampUtc">When it happened.</param>
/// <param name="Source">What produced it — the unit, the runtime, or the book.</param>
/// <param name="Message">The line itself.</param>
public readonly record struct AuthoredUnitLogLine(DateTime TimestampUtc, string Source, string Message);

/// <summary>The virtual book summary shown beneath a strategy's picture.</summary>
/// <param name="PositionUnits">Signed position; zero means flat.</param>
/// <param name="AverageEntryPrice">Average entry, or zero while flat.</param>
/// <param name="RealizedProfitAndLoss">Realised P&amp;L for the session.</param>
/// <param name="Equity">Current model equity.</param>
/// <param name="OpenOrders">Resting orders the book is waiting on.</param>
public readonly record struct AuthoredUnitBook(
    double PositionUnits,
    double AverageEntryPrice,
    double RealizedProfitAndLoss,
    double Equity,
    int OpenOrders);

/// <summary>
/// What the host needs in order to show an authored strategy or visualizer.
///
/// <para>The anatomy is deliberately the same for both, because a strategy IS a visualizer that can
/// also trade: parameters on top, the author's own picture in the middle, and — for a strategy — the
/// virtual book and trade log beneath. The author draws the middle and nothing else, so the chrome is
/// identical every time and cannot be omitted, mis-styled, or forgotten.</para>
///
/// <para><see cref="HasBook"/> is the only thing that differs between the two kinds, which is the
/// point: the difference is a capability, not a different way of building a window.</para>
/// </summary>
public sealed partial class AuthoredUnitPresenter : ObservableObject
{
    /// <summary>How many log lines are kept. Bounded because a chatty unit runs for hours.</summary>
    public const int MaximumLogLines = 500;

    /// <summary>Display name shown on the parameter expander.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>
    /// The author's frame callback — normally the unit's <c>Draw</c>. Null renders an empty surface,
    /// which is what a unit that draws nothing legitimately looks like.
    /// </summary>
    [ObservableProperty]
    private Action<IRenderSurface>? _draw;

    /// <summary>True for a strategy, false for a visualizer. Drives the book row only.</summary>
    [ObservableProperty]
    private bool _hasBook;

    [ObservableProperty]
    private AuthoredUnitBook _book;

    /// <summary>Whether the parameter expander starts open. Closed once a unit is running.</summary>
    [ObservableProperty]
    private bool _isSetupExpanded = true;

    /// <summary>Parameters the unit declared, as label/value pairs the expander renders.</summary>
    public ObservableCollection<AuthoredUnitParameter> Parameters { get; } = [];

    /// <summary>
    /// Asks the view for a repaint.
    ///
    /// <para>An event rather than a property because a frame is not state: the picture the unit would
    /// draw has changed, and nothing about the presenter has. Raised by whatever paces the unit — see
    /// <see cref="AuthoredUnitHost"/> — so the presenter never owns a timer of its own.</para>
    /// </summary>
    public event EventHandler? FrameRequested;

    /// <summary>Raises <see cref="FrameRequested"/>.</summary>
    public void RequestFrame() => FrameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The unit's own log, newest last.</summary>
    public ObservableCollection<AuthoredUnitLogLine> Log { get; } = [];

    /// <summary>
    /// Appends a log line, dropping the oldest past <see cref="MaximumLogLines"/>. Trimming here
    /// rather than in the view means a unit left running overnight cannot grow the window's memory
    /// without bound.
    /// </summary>
    public void Append(AuthoredUnitLogLine line)
    {
        Log.Add(line);
        while (Log.Count > MaximumLogLines)
            Log.RemoveAt(0);
    }
}

/// <summary>One row in the parameter expander.</summary>
public sealed partial class AuthoredUnitParameter : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;
}
