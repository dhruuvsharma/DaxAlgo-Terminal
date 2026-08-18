using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.UI;

namespace TradingTerminal.ExecutionUi;

/// <summary>One book as the header chip shows it: what it is, and whether it is taking trades.</summary>
/// <param name="Id">The book's stable identifier.</param>
/// <param name="Name">The book's display name.</param>
/// <param name="AdapterName">Paper, or the broker adapter the book routes to.</param>
/// <param name="Strategies">Bound strategies, or empty for an unbound book.</param>
/// <param name="ProfitAndLoss">Pre-formatted P&amp;L for the period the console defaults to.</param>
/// <param name="ProfitAndLossTone">Tone for <paramref name="ProfitAndLoss"/>.</param>
/// <param name="IsRunning">True when the book accepts new orders.</param>
public sealed record ExecutionBookChipReadModel(
    string Id,
    string Name,
    string AdapterName,
    IReadOnlyList<string> Strategies,
    string ProfitAndLoss,
    ExecutionTone ProfitAndLossTone,
    bool IsRunning)
{
    /// <summary>What clicking the toggle will do, phrased as the action rather than the state.</summary>
    public string ToggleLabel => IsRunning ? "Stop" : "Run";

    /// <summary>The current state, for the row's status text.</summary>
    public string StateLabel => IsRunning ? "Running" : "Stopped";

    public ExecutionTone StateTone => IsRunning ? ExecutionTone.Positive : ExecutionTone.Warning;

    /// <summary>Strategies as one line, or an honest note when the book has none bound yet.</summary>
    public string StrategySummary => Strategies.Count == 0
        ? "No strategy bound"
        : string.Join(", ", Strategies);
}

/// <summary>
/// The header's execution-books chip: every book the app-lifetime engine holds, each with a Run/Stop
/// toggle over its order intake.
///
/// <para>This exists because the engine is not the console window. The engine runs for as long as the
/// application does, so the books have to be visible and controllable without opening the console —
/// and closing the console must not stop anything. The chip reads the same
/// <see cref="IExecutionClient"/> singleton the console does, so the two can never disagree.</para>
///
/// <para>Run/Stop is order <em>intake</em>, not a kill. Stopping a book refuses new orders while
/// leaving existing positions and working orders exactly where they are; flattening is a deliberate,
/// separate act in the console.</para>
/// </summary>
public sealed partial class ExecutionBooksChipViewModel : ViewModelBase, IDisposable
{
    private readonly IExecutionClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public ExecutionBooksChipViewModel(IExecutionClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.SnapshotInvalidated += OnSnapshotInvalidated;
        Refresh();
    }

    /// <summary>Every book the engine currently holds.</summary>
    public ObservableCollection<ExecutionBookChipReadModel> Books { get; } = [];

    /// <summary>True when at least one book exists, so the chip can hide itself when there is nothing.</summary>
    [ObservableProperty]
    private bool _hasBooks;

    /// <summary>Count of books currently accepting orders — the number the chip face shows.</summary>
    [ObservableProperty]
    private int _runningCount;

    [ObservableProperty]
    private int _bookCount;

    /// <summary>Set while a toggle is in flight, so the row cannot be double-clicked into a race.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Why the last toggle was refused, shown in the popup rather than swallowed.</summary>
    [ObservableProperty]
    private string _lastError = string.Empty;

    /// <summary>True when any book is stopped, so the chip's dot can go amber rather than green.</summary>
    public bool HasStoppedBook => BookCount > 0 && RunningCount < BookCount;

    /// <summary>Summary for the chip face and its tooltip.</summary>
    public string Summary => BookCount == 0
        ? "No books"
        : $"{RunningCount}/{BookCount} running";

    [RelayCommand]
    private async Task ToggleAsync(string? bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId) || IsBusy || Volatile.Read(ref _disposed) != 0)
            return;

        var book = Books.FirstOrDefault(item => string.Equals(item.Id, bookId, StringComparison.Ordinal));
        if (book is null)
            return;

        IsBusy = true;
        LastError = string.Empty;
        try
        {
            // Pausing intake is the stop: it refuses new orders and leaves open positions alone.
            var result = await _client
                .SetIntakePausedAsync(book.Id, paused: book.IsRunning, _lifetime.Token)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
                LastError = result.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Shutting down; nothing to report.
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void OnSnapshotInvalidated(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _ = UiThread.RunAsync(Refresh);
    }

    private void Refresh()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var snapshot = _client.GetSnapshot();
        Books.Clear();
        foreach (var book in snapshot.Books)
        {
            Books.Add(new ExecutionBookChipReadModel(
                book.Id,
                book.Name,
                book.AdapterName,
                book.Strategies,
                book.ProfitAndLoss,
                book.ProfitAndLossTone,
                IsRunning: !book.IsIntakePaused));
        }

        BookCount = Books.Count;
        RunningCount = Books.Count(item => item.IsRunning);
        HasBooks = BookCount > 0;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasStoppedBook));
    }

    partial void OnRunningCountChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasStoppedBook));
    }

    partial void OnBookCountChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasStoppedBook));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The engine outlives this chip, so detaching is all this owns — disposing the client here
        // would stop every book the moment the shell shut a header control down.
        _client.SnapshotInvalidated -= OnSnapshotInvalidated;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Books.Clear();
    }
}
