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

    partial void OnStateChanged(SwarmTaskState value) => OnPropertyChanged(nameof(Glyph));
}
