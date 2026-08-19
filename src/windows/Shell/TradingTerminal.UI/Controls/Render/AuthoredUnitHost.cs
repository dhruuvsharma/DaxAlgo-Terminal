using System.Collections.Specialized;
using System.Globalization;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>
/// Drives one open unit's window: it paces the frames, fills the parameter list, and shows the unit's
/// own lines from the app-wide activity log.
///
/// <para>The unit is supplied as a <see cref="Func{T, TResult}"/> rather than an interface on purpose.
/// The only thing this needs from a running visualizer or strategy is "describe the current frame, if
/// you can" — one method — and taking it as a delegate is what keeps this library free of a dependency
/// on the sandbox runtime, so the window can be built and tested without one.</para>
///
/// <para><b>Frames are paced, not pushed.</b> Market data arrives far faster than a display can show
/// it, so redrawing on every event would burn the UI thread producing frames nobody sees. The timer
/// coalesces: whatever the unit has computed by the next tick is what gets drawn.</para>
/// </summary>
public sealed class AuthoredUnitHost : IDisposable
{
    /// <summary>Roughly 30fps. Fast enough for a live book, cheap enough to leave open all day.</summary>
    public static readonly TimeSpan DefaultFrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly Func<IRenderSurface, bool> _tryDraw;
    private readonly InMemoryLogSink? _log;
    private readonly string _logSource;
    private IDisposable? _frames;
    private int _disposed;

    /// <param name="title">The unit's display name — the expander header, and the log source it is tagged with.</param>
    /// <param name="tryDraw">Describes the current frame; false when there is nothing to draw.</param>
    /// <param name="schema">The unit's declared parameters, shown read-only while it runs.</param>
    /// <param name="values">Values in force, keyed by parameter key. Missing keys fall back to the declared default.</param>
    /// <param name="log">The app-wide activity log. The window shows this unit's slice of it.</param>
    /// <param name="hasBook">True for a strategy; shows the virtual-book row.</param>
    /// <param name="frameInterval">Overrides the frame pace. Mainly for tests.</param>
    public AuthoredUnitHost(
        string title,
        Func<IRenderSurface, bool> tryDraw,
        StrategyParameterSchema? schema = null,
        IReadOnlyDictionary<string, object?>? values = null,
        InMemoryLogSink? log = null,
        bool hasBook = false,
        TimeSpan? frameInterval = null)
    {
        ArgumentNullException.ThrowIfNull(tryDraw);
        _tryDraw = tryDraw;
        _log = log;
        _logSource = title ?? string.Empty;

        Presenter = new AuthoredUnitPresenter
        {
            Title = _logSource,
            HasBook = hasBook,
            // The picture is the point; the parameters are reference material once it is running.
            IsSetupExpanded = false,
            Draw = surface => _tryDraw(surface),
        };

        foreach (var parameter in schema?.Parameters ?? [])
            Presenter.Parameters.Add(Describe(parameter, values));

        if (_log is not null)
        {
            SeedLog();
            _log.Entries.CollectionChanged += OnLogEntryAdded;
        }

        var interval = frameInterval ?? DefaultFrameInterval;
        _frames = UiThread.CreateRenderTimer(interval, () => Presenter.RequestFrame());
    }

    /// <summary>What the window binds to.</summary>
    public AuthoredUnitPresenter Presenter { get; }

    /// <summary>
    /// Stops pacing frames. Called when the unit stops but the window stays open, so the last frame
    /// remains on screen instead of the picture vanishing the moment the feed does.
    /// </summary>
    public void Freeze()
    {
        Interlocked.Exchange(ref _frames, null)?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Freeze();
        if (_log is not null)
            _log.Entries.CollectionChanged -= OnLogEntryAdded;
    }

    /// <summary>
    /// Mirrors this unit's lines out of the app-wide sink.
    ///
    /// <para>Mirrored, not owned: there is exactly one activity log in the application and this window
    /// shows a filtered view of it. A private per-window log would drift from the main pane and give
    /// two answers to the same question.</para>
    /// </summary>
    private void OnLogEntryAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            return;

        foreach (var item in e.NewItems)
        {
            if (item is LogEntry entry && Matches(entry))
                Presenter.Append(new AuthoredUnitLogLine(entry.TimestampUtc, entry.Level, entry.Message));
        }
    }

    /// <summary>Picks up lines logged before the window opened — a start-up failure is usually one of them.</summary>
    private void SeedLog()
    {
        foreach (var entry in _log!.Entries)
        {
            if (Matches(entry))
                Presenter.Append(new AuthoredUnitLogLine(entry.TimestampUtc, entry.Level, entry.Message));
        }
    }

    private bool Matches(LogEntry entry) =>
        string.Equals(entry.Source, _logSource, StringComparison.Ordinal);

    private static AuthoredUnitParameter Describe(
        StrategyParameter parameter,
        IReadOnlyDictionary<string, object?>? values)
    {
        var value = values is not null && values.TryGetValue(parameter.Key, out var supplied)
            ? supplied
            : parameter.Default;

        return new AuthoredUnitParameter
        {
            Label = string.IsNullOrWhiteSpace(parameter.DisplayName) ? parameter.Key : parameter.DisplayName,
            Value = Format(value),
        };
    }

    private static string Format(object? value) => value switch
    {
        null => "—",
        bool flag => flag ? "on" : "off",
        double number => number.ToString("0.####", CultureInfo.CurrentCulture),
        float number => number.ToString("0.####", CultureInfo.CurrentCulture),
        decimal number => number.ToString("0.####", CultureInfo.CurrentCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
