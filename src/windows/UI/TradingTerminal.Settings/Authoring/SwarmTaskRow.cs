using CommunityToolkit.Mvvm.ComponentModel;
// Aliased: this namespace already has a BuildTask — the status bar's pipeline checklist —
// and the two are unrelated. Naming the swarm's one at the using site keeps every
// reference below unambiguous rather than fully qualified.
using PlanTask = TradingTerminal.Infrastructure.Strategies.Authoring.Swarm.BuildTask;

namespace TradingTerminal.App.Authoring;

/// <summary>Where one task stands.</summary>
public enum SwarmTaskState
{
    /// <summary>Planned, not started.</summary>
    Waiting,

    /// <summary>A builder has it now.</summary>
    Running,

    /// <summary>It produced its file.</summary>
    Done,

    /// <summary>It was asked and came back with nothing usable — a turn the user paid for.</summary>
    Empty,

    /// <summary>It is being repaired after the checks rejected something.</summary>
    Repairing,
}

/// <summary>
/// One row of the task board.
///
/// <para><b>The board is the answer to the failure that killed the committee.</b> A fan-out takes
/// minutes and reports nothing a user can read; the measured consequence was five saved sessions in
/// which somebody watched a status line and concluded the builder did not work. A row per task, updated
/// when the task STARTS and again when it finishes, is what turns that silence into something legible.</para>
/// </summary>
public sealed partial class SwarmTaskRow(PlanTask task) : ObservableObject
{
    public string Id { get; } = task.Id;

    public string Title { get; } = task.Title;

    /// <summary>Maths, Signal, Panel, Schema or Book — what sort of work this is.</summary>
    public string Kind { get; } = task.Kind.ToString();

    /// <summary>The one file this task may write. Shown because it is the merge rule made visible.</summary>
    public string File { get; } = task.OwnedFile;

    [ObservableProperty] private SwarmTaskState _state = SwarmTaskState.Waiting;

    /// <summary>What it cost, once it has cost anything.</summary>
    [ObservableProperty] private int _tokens;

    /// <summary>
    /// Characters of reasoning so far.
    ///
    /// <para><b>The number that moves when nothing else does.</b> Many providers report usage only when
    /// a call finishes, so a row can sit at "0 tok" for minutes while the model is thinking hard —
    /// which is exactly what a stalled provider also looks like. Reasoning characters climb from the
    /// first delta, so a running row that shows nothing here after a while is genuinely stuck rather
    /// than merely slow.</para>
    /// </summary>
    [ObservableProperty] private int _thinking;

    /// <summary>Characters written so far.</summary>
    [ObservableProperty] private int _written;

    /// <summary>When it started, for the elapsed clock. Null until it does.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>
    /// How long it has been running, as the board shows it.
    ///
    /// <para>Deliberately the LAST resort of proof: it moves whatever the provider does or does not
    /// report, so a row that shows nothing else at least shows how long it has shown nothing.</para>
    /// </summary>
    [ObservableProperty] private string _elapsed = string.Empty;

    /// <summary>
    /// How long a task may show NOTHING — no reasoning, no text, no tokens — before the row says so.
    ///
    /// <para>Ninety seconds because a provider that is going to answer has almost always said something
    /// by then, and because the alternative is what a user actually hit: a row that had shown nothing
    /// for minutes, with no way to tell whether it was thinking or dead.</para>
    /// </summary>
    public static readonly TimeSpan SilenceBeforeDoubt = TimeSpan.FromSeconds(90);

    /// <summary>True when this row has been running a while and has produced not one byte.</summary>
    [ObservableProperty] private bool _silent;

    /// <summary>Recomputes the clock. Called on a tick by whoever owns the board.</summary>
    public void Tick(DateTime now)
    {
        if (State is not (SwarmTaskState.Running or SwarmTaskState.Repairing) || StartedAt is not { } from)
            return;

        var span = now - from;
        Elapsed = span < TimeSpan.FromMinutes(1)
            ? $"{span.TotalSeconds:0}s"
            : $"{(int)span.TotalMinutes}m {span.Seconds:00}s";

        // NOTHING AT ALL, for a while. Not a diagnosis — a provider can legitimately think for minutes
        // before its first byte, and 278 seconds has been measured on one — but it is the difference
        // between a row that is quiet and a row that has never spoken, and only the user can decide
        // what to do about it. There is no timeout by default: Stop is the control.
        Silent = span > SilenceBeforeDoubt && Thinking == 0 && Written == 0 && Tokens == 0;
    }

    /// <summary>Why a row is not Done, when it is not.</summary>
    [ObservableProperty] private string _note = string.Empty;

    /// <summary>The glyph the board shows. Text rather than an icon set, so a row reads the same in a
    /// log, a screenshot and a bug report.</summary>
    public string Glyph => State switch
    {
        SwarmTaskState.Running => "▶",
        SwarmTaskState.Repairing => "↻",
        SwarmTaskState.Done => "✓",
        SwarmTaskState.Empty => "∅",
        _ => "·",
    };

    partial void OnStateChanged(SwarmTaskState value)
    {
        OnPropertyChanged(nameof(Glyph));

        if (value is SwarmTaskState.Running or SwarmTaskState.Repairing) StartedAt ??= DateTime.UtcNow;
        else if (value is SwarmTaskState.Done or SwarmTaskState.Empty) Elapsed = string.Empty;
    }

    /// <summary>What the board's right-hand column reads: the liveliest number this row has.</summary>
    public string Vitals => State switch
    {
        SwarmTaskState.Running or SwarmTaskState.Repairing =>
            string.Join(
                "  ",
                new[]
                {
                    Elapsed,
                    Silent ? "nothing received yet" : string.Empty,
                    Thinking > 0 ? $"{Thinking:n0} thinking" : string.Empty,
                    Written > 0 ? $"{Written:n0} written" : string.Empty,
                    Tokens > 0 ? $"{Tokens:n0} tok" : string.Empty,
                }.Where(part => part.Length > 0)),

        SwarmTaskState.Waiting => string.Empty,
        _ => Tokens > 0 ? $"{Tokens:n0} tok" : string.Empty,
    };

    partial void OnElapsedChanged(string value) => OnPropertyChanged(nameof(Vitals));

    partial void OnThinkingChanged(int value) => OnPropertyChanged(nameof(Vitals));

    partial void OnWrittenChanged(int value) => OnPropertyChanged(nameof(Vitals));

    partial void OnTokensChanged(int value) => OnPropertyChanged(nameof(Vitals));

    partial void OnSilentChanged(bool value) => OnPropertyChanged(nameof(Vitals));
}
