using System;

namespace TradingTerminal.Core.Updates;

/// <summary>
/// The read side of the update check, for whoever shows the prompt.
///
/// This exists so the view-model layer can bind to update state without referencing the
/// implementation: <c>TradingTerminal.UI.Core</c> depends on <c>Core</c> only, while the scheduler
/// that raises these events (<c>Infrastructure.Updates.UpdateCheckService</c>) sits above it. Without
/// this seam the notice view-model would drag the whole Infrastructure layer into UI.Core.
/// </summary>
public interface IUpdateNotifier
{
    /// <summary>
    /// Raised when a newer version is published — on a BACKGROUND thread. Subscribers that touch
    /// bound state must marshal to the UI thread themselves.
    /// </summary>
    event Action<UpdateCheckResult>? UpdateAvailable;

    /// <summary>
    /// The most recent result, or null before the first check has run. A subscriber created after
    /// the check (a window opened later) reads this to catch up on an event it missed.
    /// </summary>
    UpdateCheckResult? Latest { get; }
}
