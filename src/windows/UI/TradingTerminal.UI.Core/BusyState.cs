using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingTerminal.UI;

/// <summary>
/// Shared loading-state primitive for strategy/tool view-models. Bind a
/// <see cref="TradingTerminal.UI.Controls.BusyOverlay"/> to <see cref="IsActive"/>/<see cref="Title"/>/
/// <see cref="Message"/> and wrap any slow step — historical preload, a heavy recompute, a UI rebuild —
/// in a <see cref="Begin"/> scope so the user always sees a loading curtain plus a message explaining
/// exactly what is happening.
///
/// <para>Scopes are ref-counted and nestable: the curtain stays up until the outermost scope is
/// disposed, so overlapping operations don't flicker it off early. Mutate from the UI thread (the
/// usual case — commands run there); marshal via <c>UiThread.RunAsync</c> if you flip it from a
/// background continuation.</para>
///
/// <example><code>
/// using (Busy.Begin("Loading NQ", "Fetching the last 200 one-minute bars…"))
/// {
///     var bars = await Repo.GetHistoricalBarsAsync(...);
///     Busy.Report("Priming indicators…");
///     PrimeIndicators(bars);
/// }   // curtain drops here
/// </code></example>
/// </summary>
public sealed partial class BusyState : ObservableObject
{
    private int _depth;

    /// <summary>True while at least one <see cref="Begin"/> scope is open — drives the overlay.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Headline shown on the overlay (e.g. "Loading NQ").</summary>
    [ObservableProperty] private string _title = "Loading…";

    /// <summary>Sub-line describing the current step (e.g. "Fetching the last 200 bars…").</summary>
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>Opens a busy scope, setting the curtain's <paramref name="title"/> and optional
    /// <paramref name="message"/>. Dispose the returned token to close it (ref-counted/nestable).</summary>
    public IDisposable Begin(string title, string? message = null)
    {
        Title = title;
        Message = message ?? string.Empty;
        Interlocked.Increment(ref _depth);
        IsActive = true;
        return new Scope(this);
    }

    /// <summary>Updates the sub-line message mid-scope, e.g. to narrate successive load steps.</summary>
    public void Report(string message) => Message = message;

    private void End()
    {
        if (Interlocked.Decrement(ref _depth) <= 0)
        {
            _depth = 0;
            IsActive = false;
        }
    }

    private sealed class Scope(BusyState owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.End();
        }
    }
}
