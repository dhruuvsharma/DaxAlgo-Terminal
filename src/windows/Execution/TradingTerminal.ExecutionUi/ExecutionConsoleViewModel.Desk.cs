using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// The desk around the selected book: the whole-desk summary bar, the selection's value and P&amp;L split, the
/// fills, ledger and reconciliation views, the connections list, and Kill all.
/// </summary>
public sealed partial class ExecutionConsoleViewModel
{
    [ObservableProperty]
    private ExecutionDeskSummary _desk = ExecutionDeskSummary.Empty;

    [ObservableProperty]
    private ExecutionSelectionSummary _selection = ExecutionSelectionSummary.Empty;

    [ObservableProperty]
    private IReadOnlyList<ExecutionFillReadModel> _fills = Array.Empty<ExecutionFillReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionLedgerEventReadModel> _ledgerEvents = Array.Empty<ExecutionLedgerEventReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionReconciliationReadModel> _reconciliationCases = Array.Empty<ExecutionReconciliationReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionRiskUsageReadModel> _riskUsage = Array.Empty<ExecutionRiskUsageReadModel>();

    [ObservableProperty]
    private string _riskEscalation = string.Empty;

    /// <summary>Each measured fill's slippage in basis points, for the distribution chart.</summary>
    [ObservableProperty]
    private IReadOnlyList<double> _slippageSeries = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionAdapterReadModel> _visibleAdapters = Array.Empty<ExecutionAdapterReadModel>();

    [ObservableProperty]
    private string _connectionFilter = string.Empty;

    [ObservableProperty]
    private bool _showAllConnections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAdapter))]
    private ExecutionAdapterReadModel? _selectedAdapter;

    [ObservableProperty]
    private string _connectionsHeader = string.Empty;

    [ObservableProperty]
    private string _showAllConnectionsLabel = string.Empty;

    public bool HasSelectedAdapter => SelectedAdapter is not null;

    public int PositionCount => Positions.Count(item => item.Side != "FLAT");

    public int OpenOrderCount => OpenOrders.Count;

    public int FillCount => Fills.Count;

    public int OpenReconciliationCount =>
        ReconciliationCases.Count(item => !string.Equals(item.Status, "Resolved", StringComparison.Ordinal));

    public bool HasOpenReconciliation => OpenReconciliationCount > 0;

    public bool IsAllBooksSelected => SelectedBookEntry?.IsAllBooks != false;

    public bool CanKillAll => !IsBusy && BookEntries.Any(entry => entry.Book is { SupportsKill: true, Lease.IsHeld: true });

    partial void OnConnectionFilterChanged(string value) => RefreshVisibleAdapters();

    partial void OnShowAllConnectionsChanged(bool value) => RefreshVisibleAdapters();

    [RelayCommand]
    private void ToggleAllConnections() => ShowAllConnections = !ShowAllConnections;

    [RelayCommand]
    private void CloseConnection() => SelectedAdapter = null;

    /// <summary>Stops every book: intake, working orders and positions, book by book, after one confirmation.</summary>
    [RelayCommand]
    private async Task KillAllAsync()
    {
        var books = BookEntries
            .Select(entry => entry.Book)
            .OfType<ExecutionBookReadModel>()
            .Where(book => book.SupportsKill && book.Lease.IsHeld)
            .ToArray();
        if (books.Length == 0)
        {
            OperationMessage = "There is no book to kill.";
            return;
        }

        var live = books.Count(book => book.IsLive);
        var confirmed = await _confirmation.ConfirmAsync(
            "Kill every book?",
            $"Stop intake, cancel working orders and flatten positions on {books.Length} book" +
            $"{(books.Length == 1 ? string.Empty : "s")} ({string.Join(", ", books.Select(book => book.Name))})." +
            (live > 0 ? $" {live} of them {(live == 1 ? "is" : "are")} LIVE: real-money orders will be sent to flatten." : string.Empty),
            _lifetimeCancellation.Token);
        if (!confirmed)
            return;

        var failures = new List<string>();
        foreach (var book in books)
        {
            var result = await RunCommandAsync(token => _client.KillAsync(book.Id, token));
            if (!result.IsSuccess)
                failures.Add($"{book.Name}: {result.Message}");
        }

        OperationMessage = failures.Count == 0
            ? $"Killed {books.Length} book{(books.Length == 1 ? string.Empty : "s")}: intake stopped, orders cancelled, positions flat."
            : $"Kill finished with {failures.Count} failure{(failures.Count == 1 ? string.Empty : "s")} — {string.Join("; ", failures)}";
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanKillAll));

    private static ExecutionBookNavigationReadModel NavigationEntry(ExecutionBookReadModel book)
    {
        var total = book.RealizedProfitAndLoss + book.UnrealizedProfitAndLoss;
        var symbol = book.Unit.Symbol.Length > 0
            ? book.Unit.Symbol
            : book.TradableInstruments.FirstOrDefault()?.Symbol;
        var alert = book.HasStrategyWarning ? "strategy can't be used"
            : book.IsAwaitingInstrument ? "waiting for strategy"
            : book.OpenReconciliationCaseCount > 0
                ? book.OpenReconciliationCaseCount == 1 ? "1 recon case" : $"{book.OpenReconciliationCaseCount} recon cases"
            : book.IsIntakePaused ? "intake paused"
            : !book.AdmissionOpen ? "gate blocked"
            : string.Empty;
        return new ExecutionBookNavigationReadModel(
            book.Id,
            book.Name,
            book.Summary,
            ExecutionFormatting.SignedMoney(total),
            ExecutionFormatting.ToneOf(total),
            IsAllBooks: false,
            book)
        {
            // No symbol until the book's strategy names its instrument.
            Detail = symbol is null ? book.AdapterName : $"{book.AdapterName} · {symbol}",
            Position = book.PositionUnits == 0m ? "flat" : $"{ExecutionFormatting.SignedUnits(book.PositionUnits)} units",
            Strategy = book.Strategies.Count == 0
                ? "Manual only"
                : $"{book.StrategySummary} ×{book.UnitsPerStrategyUnit.ToString("N0", CultureInfo.InvariantCulture)}",
            Alert = alert,
        };
    }

    private static ExecutionDeskSummary BuildDeskSummary(ExecutionConsoleSnapshot snapshot)
    {
        var books = snapshot.Books;
        if (books.Count == 0)
            return ExecutionDeskSummary.Empty;

        var nav = books.Sum(book => book.NetAssetValue);
        var day = books.Sum(book => book.DayRealizedProfitAndLoss);
        var open = books.Sum(book => book.UnrealizedProfitAndLoss);
        var gross = books.Sum(book => book.GrossExposure);
        var net = books.Sum(book => book.NetExposure);
        var working = books.Sum(book => book.WorkingOrderCount);
        var workingBooks = books.Count(book => book.WorkingOrderCount > 0);
        var risk = snapshot.PortfolioAnalytics.Periods
            .Select(period => period.Metrics)
            .OrderByDescending(metrics => metrics.RiskObservations)
            .First();
        var brokers = snapshot.Adapters.Count(adapter => adapter.IsConnected && !adapter.IsPaper);
        return new ExecutionDeskSummary(
            ExecutionFormatting.Money(nav),
            $"{books.Count} book{(books.Count == 1 ? string.Empty : "s")} · {brokers} broker{(brokers == 1 ? string.Empty : "s")} connected",
            ExecutionFormatting.SignedMoney(day),
            ExecutionFormatting.ToneOf(day),
            ExecutionFormatting.SignedMoney(open),
            ExecutionFormatting.ToneOf(open),
            $"{ExecutionFormatting.CompactMoney(gross)} / {ExecutionFormatting.CompactMoney(net, signed: true)}",
            $"{Leverage(gross, nav)} leverage",
            risk.HasValueAtRisk ? ExecutionFormatting.Money(risk.ValueAtRisk95) : "n/a",
            risk.HasValueAtRisk
                ? nav > 0m ? $"{risk.ValueAtRisk95 / nav * 100m:0.00}% of NAV" : $"{risk.RiskObservations} days"
                : $"{risk.RiskObservations} of {ExecutionMetricResult.MinimumRiskObservations} days",
            working,
            working == 0 ? "none working" : $"on {workingBooks} book{(workingBooks == 1 ? string.Empty : "s")}",
            books.Sum(book => book.OpenReconciliationCaseCount),
            books.Sum(book => book.Analytics.ExecutionQuality.UnknownOutcomes));
    }

    private static string Leverage(decimal gross, decimal nav) =>
        nav > 0m ? $"{gross / nav:0.00}×" : "n/a";

    /// <summary>Figures and lists for the selected book, or for all books merged.</summary>
    private void ApplyDeskSelection(ExecutionBookNavigationReadModel entry, IReadOnlyList<ExecutionBookReadModel> books)
    {
        var nav = books.Sum(book => book.NetAssetValue);
        var realized = books.Sum(book => book.RealizedProfitAndLoss);
        var unrealized = books.Sum(book => book.UnrealizedProfitAndLoss);
        var day = books.Sum(book => book.DayRealizedProfitAndLoss);
        var gross = books.Sum(book => book.GrossExposure);
        var net = books.Sum(book => book.NetExposure);
        Selection = new ExecutionSelectionSummary(
            ExecutionFormatting.Money(nav),
            ExecutionFormatting.SignedMoney(realized),
            ExecutionFormatting.ToneOf(realized),
            ExecutionFormatting.SignedMoney(unrealized),
            ExecutionFormatting.ToneOf(unrealized),
            ExecutionFormatting.SignedMoney(day),
            ExecutionFormatting.ToneOf(day),
            ExecutionFormatting.CompactMoney(gross),
            ExecutionFormatting.CompactMoney(net, signed: true),
            Leverage(gross, nav));

        Fills = Array.AsReadOnly(books
            .SelectMany(book => book.Fills)
            .OrderByDescending(fill => fill.OccurredAtUtc)
            .ToArray());
        SlippageSeries = Array.AsReadOnly(Fills
            .Where(fill => fill.SlippageBasisPoints is not null)
            .Select(fill => fill.SlippageBasisPoints!.Value)
            .ToArray());
        LedgerEvents = Array.AsReadOnly(books
            .SelectMany(book => book.LedgerEvents)
            .OrderByDescending(item => item.OccurredAtUtc)
            .ToArray());
        ReconciliationCases = Array.AsReadOnly(books
            .SelectMany(book => book.ReconciliationCases)
            .ToArray());
        RiskUsage = entry.Book is { } book
            ? book.Risk.Usage
            : Array.AsReadOnly(books
                .SelectMany(item => item.Risk.Usage.Select(usage => usage with { Label = $"{item.Name} · {usage.Label}" }))
                .ToArray());
        RiskEscalation = entry.Book?.Risk.EscalationLine ?? string.Empty;

        OnPropertyChanged(nameof(PositionCount));
        OnPropertyChanged(nameof(OpenOrderCount));
        OnPropertyChanged(nameof(FillCount));
        OnPropertyChanged(nameof(OpenReconciliationCount));
        OnPropertyChanged(nameof(HasOpenReconciliation));
        OnPropertyChanged(nameof(IsAllBooksSelected));
        OnPropertyChanged(nameof(CanKillAll));
        RefreshTicket();
    }

    /// <summary>
    /// The connections the rail lists. By default the ones in use — connected, failing, or carrying a book — so
    /// forty broker cards do not bury the three that matter; when nothing is in use yet, all of them. A search
    /// always looks through everything.
    /// </summary>
    private void RefreshVisibleAdapters()
    {
        var all = Adapters.Where(adapter => adapter.IsRegistered).ToArray();
        var filter = ConnectionFilter?.Trim() ?? string.Empty;
        var inUse = all.Where(InUse).ToArray();
        IEnumerable<ExecutionAdapterReadModel> shown = filter.Length > 0
            ? all.Where(adapter =>
                adapter.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                adapter.EnvironmentLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                adapter.AccountLabel.Contains(filter, StringComparison.OrdinalIgnoreCase))
            : ShowAllConnections || inUse.Length == 0
                ? all
                : inUse;
        var ordered = shown
            .OrderBy(adapter => adapter.IsConnected ? 0 : adapter.Status == ExecutionConnectionStatus.Error ? 1 : 2)
            .ThenBy(adapter => adapter.IsPaper ? 0 : 1)
            .ThenBy(adapter => adapter.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        VisibleAdapters = Array.AsReadOnly(ordered);
        if (SelectedAdapter is { } selected)
            SelectedAdapter = all.FirstOrDefault(adapter => string.Equals(adapter.Id, selected.Id, StringComparison.Ordinal));

        var connected = all.Count(adapter => adapter.IsConnected && !adapter.IsPaper);
        ConnectionsHeader = $"{connected} of {all.Count(adapter => !adapter.IsPaper)} connected";
        var hidden = all.Length - ordered.Length;
        ShowAllConnectionsLabel = filter.Length > 0
            ? string.Empty
            : ShowAllConnections || inUse.Length == 0
                ? inUse.Length == 0 ? string.Empty : "Show only those in use"
                : hidden > 0 ? $"Show all {all.Length} (+{hidden})" : string.Empty;
    }

    private bool InUse(ExecutionAdapterReadModel adapter) =>
        adapter.IsConnected ||
        adapter.Status == ExecutionConnectionStatus.Error ||
        adapter.IsLive ||
        BookEntries.Any(entry => entry.Book is { } book && string.Equals(book.AdapterId, adapter.Id, StringComparison.Ordinal));
}
