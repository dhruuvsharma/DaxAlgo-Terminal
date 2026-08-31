using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
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
    private TimeSpan _frameInterval;
    private int _disposed;

    private Func<IReadOnlyDictionary<string, object?>, Task>? _apply;
    private Func<bool, Task>? _setPaused;
    private readonly Func<DaxAlgo.Sdk.Layout.UnitLayout>? _layout;

    /// <param name="title">The unit's display name — the expander header, and the log source it is tagged with.</param>
    /// <param name="tryDraw">Describes the current frame; false when there is nothing to draw.</param>
    /// <param name="schema">The unit's declared parameters.</param>
    /// <param name="values">Values in force, keyed by parameter key. Missing keys fall back to the declared default.</param>
    /// <param name="log">The app-wide activity log. The window shows this unit's slice of it.</param>
    /// <param name="hasBook">True for a strategy; shows the virtual-book row.</param>
    /// <param name="frameInterval">Overrides the frame pace. Mainly for tests.</param>
    /// <param name="apply">
    /// Runs the unit again with new parameter values.
    ///
    /// <para>Optional, and its absence is what keeps the rows read-only. Editing a parameter means
    /// disposing a sandbox runtime and building another, which is the shell's business — this
    /// library takes the unit as a delegate precisely so it needs no reference to the runtime, and a
    /// second delegate keeps that true. A host that does not supply one gets exactly the read-only
    /// window it had before, rather than editable boxes over values nothing reads.</para>
    /// </param>
    /// <param name="setPaused">Pauses or resumes the unit. Omitted leaves the control out.</param>
    /// <param name="layout">
    /// The unit's declared panel arrangement, asked for fresh rather than passed once.
    ///
    /// <para>A delegate because a layout does not survive a restart: applying parameters tears the
    /// session down and builds another, and the panel callbacks belong to the instance that went with
    /// it. Asked again after every apply, so a unit whose window shape depends on a parameter — two
    /// books when a second instrument is set, one when it is not — redraws as the right shape instead
    /// of keeping the old one's dead callbacks.</para>
    ///
    /// <para>Omitted means the single-panel default, which is what almost every unit wants.</para>
    /// </param>
    public AuthoredUnitHost(
        string title,
        Func<IRenderSurface, bool> tryDraw,
        StrategyParameterSchema? schema = null,
        IReadOnlyDictionary<string, object?>? values = null,
        InMemoryLogSink? log = null,
        bool hasBook = false,
        TimeSpan? frameInterval = null,
        Func<IReadOnlyDictionary<string, object?>, Task>? apply = null,
        Func<bool, Task>? setPaused = null,
        Func<DaxAlgo.Sdk.Layout.UnitLayout>? layout = null,
        Func<IReadOnlyList<UnitAction>>? actions = null,
        Func<string, Task>? invokeAction = null)
    {
        ArgumentNullException.ThrowIfNull(tryDraw);
        _tryDraw = tryDraw;
        _log = log;
        _logSource = title ?? string.Empty;
        _apply = apply;
        _setPaused = setPaused;
        _layout = layout;
        _actions = actions;
        _invokeAction = invokeAction;

        Presenter = new AuthoredUnitPresenter
        {
            Title = _logSource,
            HasBook = hasBook,
            // The picture is the point; the parameters are reference material once it is running.
            IsSetupExpanded = false,
            Draw = surface => _tryDraw(surface),
            CanEditParameters = apply is not null,
            CanPause = setPaused is not null,
        };

        foreach (var parameter in schema?.Parameters ?? [])
            Presenter.Parameters.Add(Describe(parameter, values));

        Presenter.ApplyRequested += OnApplyRequested;
        Presenter.PauseRequested += OnPauseRequested;
        Presenter.ActionRequested += OnActionRequested;

        RefreshLayout();
        RefreshActions();

        if (_log is not null)
        {
            SeedLog();
            _log.Entries.CollectionChanged += OnLogEntryAdded;
        }

        _frameInterval = frameInterval ?? DefaultFrameInterval;
        _frames = UiThread.CreateRenderTimer(_frameInterval, () => Presenter.RequestFrame());
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
        Presenter.ApplyRequested -= OnApplyRequested;
        Presenter.PauseRequested -= OnPauseRequested;
        Presenter.ActionRequested -= OnActionRequested;
        if (_log is not null)
            _log.Entries.CollectionChanged -= OnLogEntryAdded;
    }

    private async void OnApplyRequested(object? sender, IReadOnlyDictionary<string, object?> values)
    {
        if (_apply is not { } apply)
        {
            Presenter.ParametersFailed("This window cannot change parameters.");
            return;
        }

        try
        {
            await apply(values).ConfigureAwait(true);

            // Applying resumes a unit that was paused, because rebuilding the session IS a resume in
            // the runtime, and a strip reading "Paused" over a picture that is updating would be the
            // one thing this row exists to prevent.
            Presenter.IsLive = true;
            Presenter.RunState = "Live";
            Thaw();

            // The old session's panel callbacks went with it, so the shape is asked for again rather
            // than kept. A unit whose layout depends on a parameter changes shape here — and so do its
            // verbs, which belong to the instance just as the callbacks do.
            RefreshLayout();
            RefreshActions();

            Presenter.ParametersApplied("Applied — running with the new values.");
        }
        catch (Exception ex)
        {
            // An async void handler on the UI thread: an escaping exception is a process kill, and a
            // failed restart is a message, not a crash. The edits are kept for the same reason.
            Presenter.ParametersFailed($"Could not apply: {ex.Message}");
            Presenter.RunState = "Stopped";
            Presenter.IsLive = false;
        }
    }

    private async void OnPauseRequested(object? sender, bool pause)
    {
        if (_setPaused is not { } setPaused) return;

        try
        {
            await setPaused(pause).ConfigureAwait(true);
            Presenter.IsLive = !pause;
            Presenter.RunState = pause ? "Paused" : "Live";
            if (pause) Freeze(); else Thaw();
        }
        catch (Exception ex)
        {
            Presenter.ParametersFailed($"Could not {(pause ? "pause" : "resume")}: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-reads the unit's declared panel arrangement.
    ///
    /// <para>Never throws: describing a window runs author code, and a unit that fails at it keeps
    /// the single-panel default rather than losing its window.</para>
    /// </summary>
    private readonly Func<IReadOnlyList<UnitAction>>? _actions;
    private readonly Func<string, Task>? _invokeAction;

    /// <summary>
    /// Re-reads the verbs the unit declares. Called at construction and after every apply, for the
    /// same reason the layout is: an apply rebuilds the unit, and the old instance's actions belong
    /// to the instance that went with it.
    /// </summary>
    private void RefreshActions()
    {
        Presenter.Actions.Clear();
        if (_actions is null && _invokeAction is null) return;

        // Half-wired is a composition mistake, not a unit that declares nothing, and it is silent
        // otherwise: the buttons simply never appear. Said out loud in the log the user is already
        // looking at. The both-missing case cannot be detected here — the host only learns what a unit
        // declares through the very delegate that was not supplied — which is why the shell that
        // composes this needs test coverage of its own.
        if (_actions is null || _invokeAction is null)
        {
            Presenter.Append(new AuthoredUnitLogLine(
                DateTime.UtcNow, _logSource,
                "This window was wired for verbs on one side only, so none are shown."));
            return;
        }

        IReadOnlyList<UnitAction> declared;
        try
        {
            declared = UnitAction.Sanitise(_actions());
        }
        catch (Exception)
        {
            // Reading the property is author code like any other. A unit whose Actions getter throws
            // gets no buttons, not a window that fails to open.
            return;
        }

        foreach (var action in declared)
            Presenter.Actions.Add(new UnitActionButton(action.Id, action.Label, action.Detail));
    }

    /// <summary>
    /// Where a unit's take-away goes: the clipboard, and a line in the activity log saying so.
    ///
    /// <para><b>The clipboard rather than a file, deliberately.</b> "Export as CSV" on the hand-written
    /// windows means "get this data out of the window", and the clipboard satisfies that without the
    /// sandbox gaining any filesystem reach at all — no path handling, no overwrite, no disk quota, and
    /// nothing to get wrong about where an untrusted unit is allowed to write. The runtime has already
    /// bounded the size and refused anything offered outside a pressed action.</para>
    ///
    /// <para>The log line is not decoration. A clipboard that changed silently is a clipboard the
    /// viewer will paste from without knowing what they have.</para>
    /// </summary>
    public void TakeAway(string label, string text)
    {
        try
        {
            Clipboard.SetText(text);
            Presenter.Append(new AuthoredUnitLogLine(
                DateTime.UtcNow, _logSource, $"{label} copied to the clipboard ({text.Length:N0} chars)."));
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource and another process can hold it. A failed copy is
            // a message, not a fault in the unit.
            Presenter.Append(new AuthoredUnitLogLine(
                DateTime.UtcNow, _logSource, $"Could not copy {label}: {ex.Message}"));
        }
    }

    private async void OnActionRequested(object? sender, string id)
    {
        if (_invokeAction is not { } invoke) return;

        try
        {
            await invoke(id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // async void, so nothing above catches this. A button that throws logs and leaves the
            // window alone; the runtime already reports the unit's own faults.
            Presenter.Append(new AuthoredUnitLogLine(
                DateTime.UtcNow, _logSource, $"Action '{id}' failed: {ex.Message}"));
        }
    }

    private void RefreshLayout()
    {
        if (_layout is not { } read) return;

        try
        {
            Presenter.Layout = read() ?? DaxAlgo.Sdk.Layout.UnitLayout.Single;
        }
        catch (Exception)
        {
            Presenter.Layout = DaxAlgo.Sdk.Layout.UnitLayout.Single;
        }
    }

    /// <summary>Starts pacing frames again after a <see cref="Freeze"/>.</summary>
    public void Thaw()
    {
        if (_disposed != 0 || _frames is not null) return;
        _frames = UiThread.CreateRenderTimer(_frameInterval, () => Presenter.RequestFrame());
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

        var row = new AuthoredUnitParameter
        {
            Key = parameter.Key,
            Kind = parameter.Kind,
            Choices = parameter.Choices ?? [],
            Minimum = parameter.Min,
            Maximum = parameter.Max,
            Unit = parameter.Unit ?? string.Empty,
            Description = parameter.Description ?? string.Empty,
            Label = string.IsNullOrWhiteSpace(parameter.DisplayName) ? parameter.Key : parameter.DisplayName,
        };

        row.Seed(Format(value));
        return row;
    }

    /// <summary>
    /// A value as text for its editor.
    ///
    /// <para>Invariant, not current-culture, and that is the point of the change: this text is parsed
    /// back on apply, so a machine with a comma decimal separator would round-trip 1.5 into 15 or
    /// into a parse failure. It was display-only before, where the culture was right.</para>
    /// </summary>
    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("0.########", CultureInfo.InvariantCulture),
        float number => number.ToString("0.########", CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.########", CultureInfo.InvariantCulture),
        InstrumentId instrument => instrument.Value.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
