using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Globalization;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Execution;
using TradingTerminal.Execution.InteractiveBrokers;
using TradingTerminal.Execution.Oms;
using TradingTerminal.UI;

namespace TradingTerminal.ExecutionUi;

public sealed partial class ExecutionConsoleViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);
    private const int MaximumOrderPickerInstruments = 256;

    private readonly IExecutionClient _client;
    private readonly IExecutionConfirmationService _confirmation;
    private readonly IBrokerLoginFormFactory _loginFormFactory;
    private readonly ExecutionModeStatusProjection? _executionModeStatus;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<BrokerKind, IBrokerLoginForm> _loginForms = [];
    private IDisposable? _refreshTimer;
    private ExecutionConsoleSnapshot? _snapshot;
    private int _refreshPending = 1;
    private int _disposed;

    [ObservableProperty]
    private IReadOnlyList<ExecutionAdapterReadModel> _adapters = Array.Empty<ExecutionAdapterReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionBookNavigationReadModel> _bookEntries = Array.Empty<ExecutionBookNavigationReadModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBook))]
    [NotifyPropertyChangedFor(nameof(CanIssueCommands))]
    [NotifyPropertyChangedFor(nameof(CanKill))]
    [NotifyPropertyChangedFor(nameof(CanShowTestOrderTicket))]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    [NotifyPropertyChangedFor(nameof(IntakeToggleLabel))]
    [NotifyPropertyChangedFor(nameof(IsSelectedBookLive))]
    [NotifyPropertyChangedFor(nameof(OrderTicketModeLabel))]
    private ExecutionBookNavigationReadModel? _selectedBookEntry;

    [ObservableProperty]
    private bool _areBrokersExpanded;

    [ObservableProperty]
    private ExecutionTimeRange _selectedRange = ExecutionTimeRange.ThirtyDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPositionsTab))]
    [NotifyPropertyChangedFor(nameof(IsOpenOrdersTab))]
    [NotifyPropertyChangedFor(nameof(IsHistoryTab))]
    private ExecutionDetailTab _selectedTab = ExecutionDetailTab.Positions;

    [ObservableProperty]
    private ExecutionMetricResult _metrics = EmptyMetrics();

    [ObservableProperty]
    private IReadOnlyList<ExecutionEquityPointReadModel> _equitySeries = Array.Empty<ExecutionEquityPointReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionDailyPnlPointReadModel> _dailyPnlSeries = Array.Empty<ExecutionDailyPnlPointReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionExposureReadModel> _exposureByBook = Array.Empty<ExecutionExposureReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionBookBreakdownReadModel> _bookBreakdown = Array.Empty<ExecutionBookBreakdownReadModel>();

    [ObservableProperty]
    private ExecutionQualityReadModel _executionQuality = EmptyQuality();

    [ObservableProperty]
    private IReadOnlyList<ExecutionPositionReadModel> _positions = Array.Empty<ExecutionPositionReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionOrderReadModel> _openOrders = Array.Empty<ExecutionOrderReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionHistoryReadModel> _history = Array.Empty<ExecutionHistoryReadModel>();

    [ObservableProperty]
    private IReadOnlyList<ExecutionHistoryReadModel> _filteredHistory = Array.Empty<ExecutionHistoryReadModel>();

    [ObservableProperty]
    private string _historyInstrumentFilter = string.Empty;

    [ObservableProperty]
    private DateTime? _historyFromDate;

    [ObservableProperty]
    private DateTime? _historyToDate;

    [ObservableProperty]
    private bool _isNewBookOpen;

    [ObservableProperty]
    private string _newBookName = string.Empty;

    [ObservableProperty]
    private string _newBookStrategies = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ExecutionAdapterReadModel> _availableAdapters = Array.Empty<ExecutionAdapterReadModel>();

    [ObservableProperty]
    private ExecutionAdapterReadModel? _selectedNewBookAdapter;

    [ObservableProperty]
    private string _alpacaLiveAccountId = string.Empty;

    [ObservableProperty]
    private string _cTraderExecutionAccountId = string.Empty;

    [ObservableProperty]
    private string _interactiveBrokersAccountId = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ExecutionTestInstrumentReadModel> _availableTestInstruments =
        Array.Empty<ExecutionTestInstrumentReadModel>();

    [ObservableProperty]
    private IReadOnlyList<SignalInstrument> _availableOrderInstruments = Array.Empty<SignalInstrument>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private SignalInstrument? _selectedOrderInstrument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private ExecutionTestInstrumentReadModel? _selectedTestInstrument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private ExecutionTestOrderSide _selectedTestOrderSide = ExecutionTestOrderSide.Buy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestLimitOrder))]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private ExecutionTestOrderType _selectedTestOrderType = ExecutionTestOrderType.Market;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private string _testOrderQuantity = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private string _testOrderLimitPrice = string.Empty;

    [ObservableProperty]
    private string _dashboardContextLabel = "All books";

    [ObservableProperty]
    private string _connectionSummary = "Loading adapters...";

    [ObservableProperty]
    private string _leaseSummary = "Lease status unavailable";

    [ObservableProperty]
    private ExecutionTone _leaseTone = ExecutionTone.Neutral;

    [ObservableProperty]
    private string _operationMessage = "Loading the execution read model...";

    private bool _cTraderAccountBindingInitialized;
    private bool _interactiveBrokersAccountBindingInitialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIssueCommands))]
    [NotifyPropertyChangedFor(nameof(CanKill))]
    [NotifyPropertyChangedFor(nameof(CanSendTestOrder))]
    private bool _isBusy;

    public ExecutionConsoleViewModel(
        IExecutionClient client,
        IExecutionConfirmationService confirmation,
        IBrokerLoginFormFactory loginFormFactory,
        ExecutionModeStatusProjection? executionModeStatus = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
        _loginFormFactory = loginFormFactory ?? throw new ArgumentNullException(nameof(loginFormFactory));
        _executionModeStatus = executionModeStatus;
        _client.SnapshotInvalidated += OnSnapshotInvalidated;
        if (_executionModeStatus is not null)
            _executionModeStatus.PropertyChanged += OnExecutionModeStatusChanged;
        try
        {
            RefreshNow();
            _refreshTimer = UiThread.CreateRenderTimer(RefreshInterval, OnRefreshTick);
        }
        catch
        {
            _client.SnapshotInvalidated -= OnSnapshotInvalidated;
            if (_executionModeStatus is not null)
                _executionModeStatus.PropertyChanged -= OnExecutionModeStatusChanged;
            DisposeLoginForms();
            _client.Dispose();
            throw;
        }
    }

    public event EventHandler? ChartsInvalidated;

    public event EventHandler? Disposing;

    public event EventHandler? CredentialInputsCleared;

    public ExecutionBookReadModel? SelectedBook => SelectedBookEntry?.Book;

    public bool CanIssueCommands => !IsBusy && SelectedBook?.Lease.IsHeld == true;

    public bool CanKill => CanIssueCommands && SelectedBook?.SupportsKill == true;

    public bool CanShowTestOrderTicket => SelectedBook?.TestInstruments.Count > 0;

    public bool CanSendTestOrder =>
        !IsBusy &&
        SelectedBook?.CanSubmitTestOrder == true &&
        ResolveSelectedTestInstrument(SelectedBook, SelectedOrderInstrument) is not null;

    public bool IsTestLimitOrder => SelectedTestOrderType == ExecutionTestOrderType.Limit;

    public IReadOnlyList<ExecutionTestOrderSide> TestOrderSides { get; } =
        Array.AsReadOnly(Enum.GetValues<ExecutionTestOrderSide>());

    public IReadOnlyList<ExecutionTestOrderType> TestOrderTypes { get; } =
        Array.AsReadOnly(Enum.GetValues<ExecutionTestOrderType>());

    public string IntakeToggleLabel => SelectedBook?.IntakeCommandLabel ?? "Stop";

    public bool IsSelectedBookLive => SelectedBook?.IsLive == true;

    public string OrderTicketModeLabel => IsSelectedBookLive ? "LIVE ORDER" : "PAPER / TEST ORDER";

    public bool HasLiveExecution =>
        _executionModeStatus?.HasLiveExecution ?? _snapshot?.HasLiveExecution == true;

    public string ExecutionModeBannerLabel => HasLiveExecution
        ? "LIVE — REAL-MONEY EXECUTION ENABLED"
        : "PAPER — SAFE DEFAULT";

    public ExecutionTone ExecutionModeBannerTone =>
        HasLiveExecution ? ExecutionTone.Negative : ExecutionTone.Info;

    public bool IsPositionsTab => SelectedTab == ExecutionDetailTab.Positions;

    public bool IsOpenOrdersTab => SelectedTab == ExecutionDetailTab.OpenOrders;

    public bool IsHistoryTab => SelectedTab == ExecutionDetailTab.History;

    public string AnalyticsProvenanceLabel =>
        "SAMPLE-ONLY portfolio analytics use representative in-memory trade outcomes; operational history and execution quality are exact OMS projections.";

    partial void OnSelectedBookEntryChanged(ExecutionBookNavigationReadModel? value) => ApplyDashboard();

    partial void OnSelectedRangeChanged(ExecutionTimeRange value) => ApplyDashboard();

    partial void OnHistoryInstrumentFilterChanged(string value) => ApplyHistoryFilter();

    partial void OnHistoryFromDateChanged(DateTime? value) => ApplyHistoryFilter();

    partial void OnHistoryToDateChanged(DateTime? value) => ApplyHistoryFilter();

    partial void OnSelectedOrderInstrumentChanged(SignalInstrument? value)
    {
        SelectedTestInstrument = ResolveSelectedTestInstrument(SelectedBook, value);
        OnPropertyChanged(nameof(CanSendTestOrder));
    }

    [RelayCommand]
    private void ToggleBrokers() => AreBrokersExpanded = !AreBrokersExpanded;

    [RelayCommand]
    private void NewBook()
    {
        IsNewBookOpen = true;
        SelectedNewBookAdapter ??= AvailableAdapters.FirstOrDefault();
    }

    [RelayCommand]
    private void CancelNewBook()
    {
        IsNewBookOpen = false;
        NewBookName = string.Empty;
        NewBookStrategies = string.Empty;
    }

    [RelayCommand]
    private async Task CreateBookAsync()
    {
        if (SelectedNewBookAdapter is null)
        {
            OperationMessage = "Select an available execution adapter before creating a book.";
            return;
        }

        var strategies = NewBookStrategies
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new ExecutionBookCreateRequest(
            NewBookName,
            SelectedNewBookAdapter.Id,
            Array.AsReadOnly(strategies));
        var result = await RunCommandAsync(token => _client.CreateBookAsync(request, token));
        if (result.IsSuccess)
            CancelNewBook();
    }

    [RelayCommand]
    private async Task ConnectAdapterAsync(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
            return;

        var adapter = Adapters.FirstOrDefault(item => string.Equals(item.Id, adapterId, StringComparison.Ordinal));
        if (adapter is null)
            return;
        if (!TryReadBrokerCredentials(
                adapter,
                adapter.Mode,
                mapInteractiveBrokersPort: false,
                out var credentials,
                out var validationFailure) ||
            !TryResolveConnectAccountBinding(adapter, out var accountId, out validationFailure))
        {
            OperationMessage = validationFailure;
            return;
        }

        var request = new ExecutionAdapterConnectRequest(
            adapterId,
            KeyId: credentials.KeyId,
            SecretKey: credentials.SecretKey,
            Host: credentials.Host,
            Port: credentials.Port,
            ClientId: credentials.ClientId,
            AccountId: accountId,
            OAuthClientId: credentials.OAuthClientId,
            OAuthClientSecret: credentials.OAuthClientSecret,
            OAuthAccessToken: credentials.OAuthAccessToken);
        try
        {
            await RunCommandAsync(token => _client.ConnectAdapterAsync(request, token));
        }
        finally
        {
            ClearSensitiveCredentials(adapter.LoginForm);
        }
    }

    [RelayCommand]
    private async Task DisconnectAdapterAsync(string? adapterId)
    {
        if (!string.IsNullOrWhiteSpace(adapterId))
            await RunCommandAsync(token => _client.DisconnectAdapterAsync(adapterId, token));
    }

    [RelayCommand]
    private async Task SetExecutionModeAsync(ExecutionAdapterReadModel? adapter)
    {
        if (adapter is null || IsBusy || !adapter.CanChangeExecutionMode)
            return;

        var requestedMode = adapter.IsLive ? ExecutionMode.Paper : ExecutionMode.Live;
        if (!TryReadBrokerCredentials(
                adapter,
                requestedMode,
                mapInteractiveBrokersPort: true,
                out var credentials,
                out var validationFailure) ||
            !TryResolveModeAccountBinding(adapter, requestedMode, out var confirmationAccount, out validationFailure))
        {
            OperationMessage = validationFailure;
            return;
        }

        var typedConfirmation = string.Empty;
        if (requestedMode == ExecutionMode.Live)
        {
            var confirmation = await _confirmation.ConfirmTypedAsync(
                "Enable LIVE execution?",
                $"Type LIVE to route real-money orders on {adapter.DisplayName} account " +
                $"'{confirmationAccount}'. Paper remains active unless the backend " +
                "independently verifies the owner option, real credentials, and persisted confirmation.",
                "LIVE",
                _lifetimeCancellation.Token);
            if (!confirmation.IsConfirmed ||
                !string.Equals(confirmation.EnteredText, "LIVE", StringComparison.Ordinal))
            {
                OperationMessage = "LIVE execution was not enabled; this connection remains PAPER.";
                return;
            }

            typedConfirmation = confirmation.EnteredText;
        }

        var request = new ExecutionModeChangeRequest(
            adapterId: adapter.Id,
            accountId: confirmationAccount,
            mode: requestedMode,
            typedConfirmation: typedConfirmation,
            keyId: credentials.KeyId,
            secretKey: credentials.SecretKey,
            host: credentials.Host,
            port: credentials.Port,
            clientId: credentials.ClientId,
            oauthClientId: credentials.OAuthClientId,
            oauthClientSecret: credentials.OAuthClientSecret,
            oauthAccessToken: credentials.OAuthAccessToken);
        try
        {
            var result = await RunCommandAsync(token => _client.SetExecutionModeAsync(request, token));
            if (result.IsSuccess && adapter.LoginForm is IbLoginFormViewModel interactiveBrokersForm)
                interactiveBrokersForm.Port = credentials.Port;
        }
        finally
        {
            ClearSensitiveCredentials(adapter.LoginForm);
        }
    }

    [RelayCommand]
    private void SelectRange(ExecutionTimeRange range) => SelectedRange = range;

    [RelayCommand]
    private void SelectTab(ExecutionDetailTab tab) => SelectedTab = tab;

    [RelayCommand]
    private async Task ToggleIntakeAsync()
    {
        var book = SelectedBook;
        if (book is null || !CanIssueCommands)
            return;

        var pause = !book.IsIntakePaused;
        var confirmed = await _confirmation.ConfirmAsync(
            pause ? "Stop new-order intake?" : "Start new-order intake?",
            pause
                ? $"Stop new-order intake for book '{book.Name}'? Existing working orders remain managed."
                : $"Start order intake for book '{book.Name}'? Reconciliation, risk, and lease gates still apply.",
            _lifetimeCancellation.Token);
        if (confirmed)
            await RunCommandAsync(token => _client.SetIntakePausedAsync(book.Id, pause, token));
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        var book = SelectedBook;
        if (book is not null && CanIssueCommands)
            await RunCommandAsync(token => _client.ReconcileAsync(book.Id, token));
    }

    [RelayCommand]
    private async Task KillAsync()
    {
        var book = SelectedBook;
        if (book is null || !CanKill)
            return;

        var confirmed = await _confirmation.ConfirmAsync(
            "Activate the kill switch?",
            $"Kill book '{book.Name}' through its existing {book.AdapterName} execution client? " +
            "This stops new-order intake, cancels working orders, and flattens every position. " +
            "Risk, reconciliation, lease, and fencing guardrails remain active.",
            _lifetimeCancellation.Token);
        if (confirmed)
            await RunCommandAsync(token => _client.KillAsync(book.Id, token));
    }

    [RelayCommand]
    private async Task SendTestOrderAsync()
    {
        var book = SelectedBook;
        var instrument = ResolveSelectedTestInstrument(book, SelectedOrderInstrument);
        if (book is null || instrument is null)
        {
            OperationMessage =
                "Order refused: select exactly one catalog instrument certified for the selected book.";
            return;
        }
        if (!CanSendTestOrder)
            return;

        if (!long.TryParse(
                TestOrderQuantity.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var quantity) ||
            quantity <= 0)
        {
            OperationMessage = "Paper/test quantity must be a positive whole number.";
            return;
        }

        ScaledPrice? limitPrice = null;
        if (SelectedTestOrderType == ExecutionTestOrderType.Limit)
        {
            if (!TryParsePositiveScaledPrice(TestOrderLimitPrice, out var parsedLimit))
            {
                OperationMessage = "Enter a positive exact decimal limit price (up to 18 decimal places).";
                return;
            }
            limitPrice = parsedLimit;
        }

        var request = new ExecutionTestOrderRequest(
            book.Id,
            instrument.Instrument,
            instrument.Symbol,
            SelectedTestOrderSide,
            ScaledQuantity.FromWhole(quantity),
            SelectedTestOrderType,
            limitPrice);
        await RunCommandAsync(token => _client.SubmitTestOrderAsync(request, token));
    }

    private async Task<ExecutionCommandResult> RunCommandAsync(
        Func<CancellationToken, ValueTask<ExecutionCommandResult>> operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return ExecutionCommandResult.Failure("The execution console is closed.");

        IsBusy = true;
        try
        {
            var result = await operation(_lifetimeCancellation.Token);
            OperationMessage = result.Message;
            Interlocked.Exchange(ref _refreshPending, 1);
            return result;
        }
        catch (OperationCanceledException)
        {
            OperationMessage = "Execution console operation cancelled.";
            return ExecutionCommandResult.Failure(OperationMessage);
        }
        catch (Exception exception)
        {
            OperationMessage = $"Execution console operation failed safely: {exception.Message}";
            return ExecutionCommandResult.Failure(OperationMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnSnapshotInvalidated(object? sender, EventArgs e) =>
        Interlocked.Exchange(ref _refreshPending, 1);

    private void OnExecutionModeStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionModeStatusProjection.HasLiveExecution), StringComparison.Ordinal))
        {
            NotifyExecutionModeState();
        }
    }

    private void OnRefreshTick()
    {
        if (Interlocked.Exchange(ref _refreshPending, 0) != 0)
            RefreshNow();
    }

    private void RefreshNow()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var selectedId = SelectedBookEntry?.Id ?? "all";
        var snapshot = _client.GetSnapshot();
        _snapshot = snapshot;
        var adapters = AttachLoginForms(snapshot.Adapters);
        Adapters = adapters;
        InitializeExecutionAccountBindings(adapters);
        NotifyExecutionModeState();
        AvailableAdapters = Array.AsReadOnly(adapters
            .Where(item => item.IsRegistered && item.CanCreateBook)
            .ToArray());
        SelectedNewBookAdapter = AvailableAdapters.FirstOrDefault(item =>
                                     item.Id == SelectedNewBookAdapter?.Id) ??
                                 AvailableAdapters.FirstOrDefault();

        var allPeriod = snapshot.PortfolioAnalytics.Period(ExecutionTimeRange.ThirtyDays);
        var entries = new List<ExecutionBookNavigationReadModel>
        {
            new(
                "all",
                "All books",
                $"{snapshot.Books.Count} books  |  {snapshot.Adapters.Count(item => item.IsRegistered)} execution adapters",
                allPeriod.Metrics.NetProfitAndLossDisplay,
                allPeriod.Metrics.ProfitAndLossTone,
                IsAllBooks: true,
                Book: null),
        };
        entries.AddRange(snapshot.Books.Select(book => new ExecutionBookNavigationReadModel(
            book.Id,
            book.Name,
            book.Summary,
            book.ProfitAndLoss,
            book.ProfitAndLossTone,
            IsAllBooks: false,
            book)));
        BookEntries = Array.AsReadOnly(entries.ToArray());
        SelectedBookEntry = BookEntries.FirstOrDefault(item => item.Id == selectedId) ?? BookEntries[0];

        ConnectionSummary = $"{snapshot.Adapters.Count(item => item.IsConnected)} connected  |  " +
                            $"{snapshot.Adapters.Count(item => item.IsUnavailable)} unavailable";
        if (!string.IsNullOrWhiteSpace(snapshot.LastOperationMessage))
            OperationMessage = snapshot.LastOperationMessage;
        ApplyDashboard();
    }

    private void ApplyDashboard()
    {
        var snapshot = _snapshot;
        var selectedEntry = SelectedBookEntry;
        if (snapshot is null || selectedEntry is null)
            return;

        var selectedBooks = selectedEntry.Book is { } book
            ? new[] { book }
            : snapshot.Books.ToArray();
        ApplyTestTicket(selectedEntry.Book);
        var analytics = selectedEntry.Book?.Analytics ?? snapshot.PortfolioAnalytics;
        if (analytics.Periods.Count == 0)
            return;
        var period = analytics.Period(SelectedRange);
        Metrics = period.Metrics;
        EquitySeries = period.EquitySeries;
        DailyPnlSeries = period.DailyProfitAndLossSeries;
        ExposureByBook = analytics.ExposureByBook;
        ExecutionQuality = analytics.ExecutionQuality;
        Positions = Array.AsReadOnly(selectedBooks.SelectMany(item => item.Positions).ToArray());
        OpenOrders = Array.AsReadOnly(selectedBooks
            .SelectMany(item => item.Orders)
            .Where(item => item.IsOpen)
            .OrderByDescending(item => item.LastUpdatedUtc)
            .ToArray());
        History = Array.AsReadOnly(selectedBooks
            .SelectMany(item => item.History)
            .OrderByDescending(item => item.OccurredAtUtc)
            .ToArray());
        ApplyHistoryFilter();

        BookBreakdown = Array.AsReadOnly(selectedBooks.Select(item =>
        {
            var itemPeriod = item.Analytics.Period(SelectedRange);
            var day = itemPeriod.DailyProfitAndLossSeries.LastOrDefault()?.RealizedProfitAndLoss ?? 0m;
            var dayTone = day > 0m ? ExecutionTone.Positive : day < 0m ? ExecutionTone.Negative : ExecutionTone.Neutral;
            return new ExecutionBookBreakdownReadModel(
                item.Id,
                item.Name,
                item.AdmissionTone,
                itemPeriod.Metrics.EquityDisplay,
                ExecutionFormatting.SignedMoney(day),
                dayTone,
                itemPeriod.Metrics.ReturnDisplay,
                itemPeriod.Metrics.ProfitAndLossTone,
                itemPeriod.Metrics.SharpeDisplay,
                itemPeriod.Metrics.TradeCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray());

        DashboardContextLabel = selectedEntry.IsAllBooks
            ? $"All books  |  {selectedBooks.Length} books"
            : $"{selectedEntry.Name}  |  {selectedEntry.Book!.AdapterName}  |  {selectedEntry.Book.StrategySummary}";
        if (selectedEntry.Book is { } selectedBook)
        {
            LeaseSummary = $"Lease {selectedBook.Lease.StatusLabel.ToLowerInvariant()}  |  {selectedBook.Lease.FenceLabel}";
            LeaseTone = selectedBook.Lease.IsHeld ? ExecutionTone.Positive : ExecutionTone.Warning;
        }
        else
        {
            var held = selectedBooks.Count(item => item.Lease.IsHeld);
            LeaseSummary = $"{held}/{selectedBooks.Length} book leases held";
            LeaseTone = held == selectedBooks.Length ? ExecutionTone.Positive : ExecutionTone.Warning;
        }

        OnPropertyChanged(nameof(SelectedBook));
        OnPropertyChanged(nameof(CanIssueCommands));
        OnPropertyChanged(nameof(CanKill));
        OnPropertyChanged(nameof(CanShowTestOrderTicket));
        OnPropertyChanged(nameof(CanSendTestOrder));
        OnPropertyChanged(nameof(IntakeToggleLabel));
        OnPropertyChanged(nameof(IsSelectedBookLive));
        OnPropertyChanged(nameof(OrderTicketModeLabel));
        ChartsInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTestTicket(ExecutionBookReadModel? book)
    {
        var selectedSymbol = SelectedOrderInstrument?.Contract.Symbol;
        AvailableTestInstruments = Array.AsReadOnly((book?.TestInstruments ?? [])
            .Take(MaximumOrderPickerInstruments)
            .ToArray());

        var remainingSymbols = AvailableTestInstruments
            .Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);
        if (remainingSymbols.Count == 0)
        {
            AvailableOrderInstruments = Array.Empty<SignalInstrument>();
            SelectedOrderInstrument = null;
            SelectedTestInstrument = null;
            return;
        }

        var pickerItems = new List<SignalInstrument>(Math.Min(
            remainingSymbols.Count,
            MaximumOrderPickerInstruments));
        foreach (var item in SignalInstrumentCatalog.All)
        {
            if (!remainingSymbols.Remove(item.Contract.Symbol))
                continue;

            pickerItems.Add(item);
            if (pickerItems.Count >= MaximumOrderPickerInstruments || remainingSymbols.Count == 0)
                break;
        }

        AvailableOrderInstruments = Array.AsReadOnly(pickerItems.ToArray());
        SelectedOrderInstrument = selectedSymbol is not null
            ? AvailableOrderInstruments.FirstOrDefault(item =>
                  string.Equals(item.Contract.Symbol, selectedSymbol, StringComparison.Ordinal)) ??
              AvailableOrderInstruments.FirstOrDefault()
            : AvailableOrderInstruments.FirstOrDefault();
        SelectedTestInstrument = ResolveSelectedTestInstrument(book, SelectedOrderInstrument);
    }

    private static ExecutionTestInstrumentReadModel? ResolveSelectedTestInstrument(
        ExecutionBookReadModel? book,
        SignalInstrument? selected)
    {
        if (book is null || selected is null)
            return null;

        var matches = book.TestInstruments
            .Where(item => string.Equals(
                item.Symbol,
                selected.Contract.Symbol,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void NotifyExecutionModeState()
    {
        OnPropertyChanged(nameof(HasLiveExecution));
        OnPropertyChanged(nameof(ExecutionModeBannerLabel));
        OnPropertyChanged(nameof(ExecutionModeBannerTone));
    }

    private IReadOnlyList<ExecutionAdapterReadModel> AttachLoginForms(
        IReadOnlyList<ExecutionAdapterReadModel> adapters)
    {
        var activeBrokers = new HashSet<BrokerKind>();
        var attached = new ExecutionAdapterReadModel[adapters.Count];
        for (var index = 0; index < adapters.Count; index++)
        {
            var adapter = adapters[index];
            if (adapter.LoginBroker is not { } broker)
            {
                attached[index] = adapter;
                continue;
            }

            activeBrokers.Add(broker);
            if (!_loginForms.TryGetValue(broker, out var form))
            {
                form = _loginFormFactory.Get(broker);
                _loginForms.Add(broker, form);
            }
            attached[index] = adapter with { LoginForm = form };
        }

        foreach (var broker in _loginForms.Keys.Where(broker => !activeBrokers.Contains(broker)).ToArray())
        {
            DisposeLoginForm(_loginForms[broker]);
            _loginForms.Remove(broker);
        }
        return Array.AsReadOnly(attached);
    }

    private void InitializeExecutionAccountBindings(IReadOnlyList<ExecutionAdapterReadModel> adapters)
    {
        if (!_cTraderAccountBindingInitialized &&
            adapters.FirstOrDefault(item => item.LoginBroker == BrokerKind.CTrader) is { } cTrader)
        {
            CTraderExecutionAccountId = cTrader.BrokerAccountId;
            _cTraderAccountBindingInitialized = true;
        }
        if (!_interactiveBrokersAccountBindingInitialized &&
            adapters.FirstOrDefault(item => item.LoginBroker == BrokerKind.InteractiveBrokers) is { } interactiveBrokers)
        {
            InteractiveBrokersAccountId = interactiveBrokers.BrokerAccountId;
            _interactiveBrokersAccountBindingInitialized = true;
        }
    }

    private bool TryReadBrokerCredentials(
        ExecutionAdapterReadModel adapter,
        ExecutionMode mode,
        bool mapInteractiveBrokersPort,
        out BrokerCredentialInput credentials,
        out string failure)
    {
        credentials = default;
        failure = string.Empty;
        switch (adapter.LoginBroker)
        {
            case null:
                return true;
            case BrokerKind.Alpaca when adapter.LoginForm is AlpacaLoginFormViewModel alpaca:
            {
                var keyId = alpaca.ApiKey?.Trim() ?? string.Empty;
                var secretKey = alpaca.ApiSecret?.Trim() ?? string.Empty;
                if (keyId.Length == 0 || secretKey.Length == 0)
                {
                    failure = "Enter both Alpaca key ID and secret in the shared Login form before connecting.";
                    return false;
                }
                credentials = new BrokerCredentialInput(KeyId: keyId, SecretKey: secretKey);
                return true;
            }
            case BrokerKind.CTrader when adapter.LoginForm is CTraderLoginFormViewModel cTrader:
            {
                var clientId = cTrader.ClientId?.Trim() ?? string.Empty;
                var clientSecret = cTrader.ClientSecret?.Trim() ?? string.Empty;
                var accessToken = cTrader.AccessToken?.Trim() ?? string.Empty;
                if (clientId.Length == 0 || clientSecret.Length == 0 || accessToken.Length == 0)
                {
                    failure = "Enter the cTrader OAuth client ID, client secret, and access token in the shared Login form before connecting.";
                    return false;
                }
                credentials = new BrokerCredentialInput(
                    OAuthClientId: clientId,
                    OAuthClientSecret: clientSecret,
                    OAuthAccessToken: accessToken);
                return true;
            }
            case BrokerKind.InteractiveBrokers when adapter.LoginForm is IbLoginFormViewModel interactiveBrokers:
            {
                var host = interactiveBrokers.Host?.Trim() ?? string.Empty;
                if (host.Length == 0)
                {
                    failure = "Enter the Interactive Brokers TWS or Gateway host in the shared Login form before connecting.";
                    return false;
                }
                if (interactiveBrokers.ClientId < 0)
                {
                    failure = "Interactive Brokers client ID must be zero or greater.";
                    return false;
                }
                if (!TryResolveInteractiveBrokersModePort(
                        interactiveBrokers.Port,
                        mode,
                        out var exactPort,
                        out failure))
                {
                    return false;
                }
                if (!mapInteractiveBrokersPort && exactPort != interactiveBrokers.Port)
                {
                    failure = mode == ExecutionMode.Live
                        ? "Interactive Brokers LIVE requires TWS port 7496 or Gateway port 4001."
                        : "Interactive Brokers PAPER requires TWS port 7497 or Gateway port 4002.";
                    return false;
                }
                credentials = new BrokerCredentialInput(
                    Host: host,
                    Port: exactPort,
                    ClientId: interactiveBrokers.ClientId);
                return true;
            }
            default:
                failure = $"The shared Login form for {adapter.DisplayName} is unavailable or has an unexpected type.";
                return false;
        }
    }

    private bool TryResolveConnectAccountBinding(
        ExecutionAdapterReadModel adapter,
        out string accountId,
        out string failure)
    {
        switch (adapter.LoginBroker)
        {
            case BrokerKind.CTrader:
                return TryResolveCTraderAccountId(out accountId, out failure);
            case BrokerKind.InteractiveBrokers:
                return TryResolveExactAccountId(
                    InteractiveBrokersAccountId,
                    "Interactive Brokers",
                    out accountId,
                    out failure);
            default:
                accountId = string.Empty;
                failure = string.Empty;
                return true;
        }
    }

    private bool TryResolveModeAccountBinding(
        ExecutionAdapterReadModel adapter,
        ExecutionMode mode,
        out string accountId,
        out string failure)
    {
        switch (adapter.LoginBroker)
        {
            case BrokerKind.Alpaca when mode == ExecutionMode.Live:
                if (!TryResolveExactAccountId(AlpacaLiveAccountId, "Alpaca LIVE", out accountId, out failure))
                {
                    failure = "LIVE execution remains PAPER: enter the exact expected Alpaca LIVE account ID first.";
                    return false;
                }
                return true;
            case BrokerKind.CTrader:
                return TryResolveCTraderAccountId(out accountId, out failure);
            case BrokerKind.InteractiveBrokers:
                return TryResolveExactAccountId(
                    InteractiveBrokersAccountId,
                    "Interactive Brokers",
                    out accountId,
                    out failure);
            default:
                return TryResolveExactAccountId(
                    adapter.ConfirmationAccountLabel,
                    adapter.DisplayName,
                    out accountId,
                    out failure);
        }
    }

    private bool TryResolveCTraderAccountId(out string accountId, out string failure)
    {
        var value = CTraderExecutionAccountId.Trim();
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0 ||
            !string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            accountId = string.Empty;
            failure = "Enter the exact positive canonical cTrader execution account ID before connecting.";
            return false;
        }
        accountId = value;
        failure = string.Empty;
        return true;
    }

    private static bool TryResolveExactAccountId(
        string? value,
        string brokerLabel,
        out string accountId,
        out string failure)
    {
        accountId = value?.Trim() ?? string.Empty;
        if (accountId.Length == 0 ||
            accountId.Length > LiveExecutionConfirmation.MaximumAccountIdLength ||
            !string.Equals(accountId, value, StringComparison.Ordinal))
        {
            accountId = string.Empty;
            failure = $"Enter one exact bounded {brokerLabel} account ID before connecting.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TryResolveInteractiveBrokersModePort(
        int currentPort,
        ExecutionMode mode,
        out int port,
        out string failure)
    {
        port = 0;
        failure = string.Empty;
        if (!Enum.IsDefined(mode))
        {
            failure = "The Interactive Brokers execution mode is invalid.";
            return false;
        }

        port = currentPort switch
        {
            InteractiveBrokersExecutionOptions.TwsLivePort or
                InteractiveBrokersExecutionOptions.TwsPaperPort =>
                mode == ExecutionMode.Live
                    ? InteractiveBrokersExecutionOptions.TwsLivePort
                    : InteractiveBrokersExecutionOptions.TwsPaperPort,
            InteractiveBrokersExecutionOptions.GatewayLivePort or
                InteractiveBrokersExecutionOptions.GatewayPaperPort =>
                mode == ExecutionMode.Live
                    ? InteractiveBrokersExecutionOptions.GatewayLivePort
                    : InteractiveBrokersExecutionOptions.GatewayPaperPort,
            _ => 0,
        };
        if (port != 0)
            return true;

        failure = "Interactive Brokers accepts only TWS ports 7497/7496 or Gateway ports 4002/4001.";
        return false;
    }

    private void ClearSensitiveCredentials(IBrokerLoginForm? form)
    {
        switch (form)
        {
            case AlpacaLoginFormViewModel alpaca:
                alpaca.ApiSecret = string.Empty;
                break;
            case CTraderLoginFormViewModel cTrader:
                cTrader.ClientSecret = string.Empty;
                cTrader.AccessToken = string.Empty;
                break;
            case IbLoginFormViewModel interactiveBrokers:
                interactiveBrokers.Password = string.Empty;
                break;
            default:
                return;
        }
        CredentialInputsCleared?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeLoginForms()
    {
        foreach (var form in _loginForms.Values.Distinct().ToArray())
            DisposeLoginForm(form);
        _loginForms.Clear();
    }

    private static void DisposeLoginForm(IBrokerLoginForm form)
    {
        switch (form)
        {
            case AlpacaLoginFormViewModel alpaca:
                alpaca.Username = string.Empty;
                alpaca.ApiKey = string.Empty;
                alpaca.ApiSecret = string.Empty;
                break;
            case CTraderLoginFormViewModel cTrader:
                cTrader.Username = string.Empty;
                cTrader.ClientId = string.Empty;
                cTrader.ClientSecret = string.Empty;
                cTrader.AccessToken = string.Empty;
                cTrader.AccountId = 0;
                break;
            case IbLoginFormViewModel interactiveBrokers:
                interactiveBrokers.Username = string.Empty;
                interactiveBrokers.Password = string.Empty;
                interactiveBrokers.Host = string.Empty;
                interactiveBrokers.Port = 0;
                interactiveBrokers.ClientId = 0;
                interactiveBrokers.RememberPassword = false;
                break;
        }
        if (form is IDisposable disposable)
            disposable.Dispose();
    }

    private void ApplyHistoryFilter()
    {
        var query = History.AsEnumerable();
        var instrument = HistoryInstrumentFilter.Trim();
        if (instrument.Length > 0)
        {
            query = query.Where(item =>
                item.Instrument.Contains(instrument, StringComparison.OrdinalIgnoreCase) ||
                item.BookName.Contains(instrument, StringComparison.OrdinalIgnoreCase));
        }
        if (HistoryFromDate is { } from)
            query = query.Where(item => item.OccurredAtUtc.Date >= from.Date);
        if (HistoryToDate is { } to)
            query = query.Where(item => item.OccurredAtUtc.Date <= to.Date);
        FilteredHistory = Array.AsReadOnly(query.ToArray());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetimeCancellation.Cancel();
        DisposeLoginForms();
        Disposing?.Invoke(this, EventArgs.Empty);
        _client.SnapshotInvalidated -= OnSnapshotInvalidated;
        if (_executionModeStatus is not null)
            _executionModeStatus.PropertyChanged -= OnExecutionModeStatusChanged;
        Interlocked.Exchange(ref _refreshTimer, null)?.Dispose();
        _client.Dispose();
        _lifetimeCancellation.Dispose();
        AlpacaLiveAccountId = string.Empty;
        CTraderExecutionAccountId = string.Empty;
        InteractiveBrokersAccountId = string.Empty;
        TestOrderLimitPrice = string.Empty;
        _snapshot = null;
        Adapters = Array.Empty<ExecutionAdapterReadModel>();
        BookEntries = Array.Empty<ExecutionBookNavigationReadModel>();
        EquitySeries = Array.Empty<ExecutionEquityPointReadModel>();
        DailyPnlSeries = Array.Empty<ExecutionDailyPnlPointReadModel>();
        Positions = Array.Empty<ExecutionPositionReadModel>();
        OpenOrders = Array.Empty<ExecutionOrderReadModel>();
        History = Array.Empty<ExecutionHistoryReadModel>();
        FilteredHistory = Array.Empty<ExecutionHistoryReadModel>();
        AvailableTestInstruments = Array.Empty<ExecutionTestInstrumentReadModel>();
        AvailableOrderInstruments = Array.Empty<SignalInstrument>();
        SelectedOrderInstrument = null;
        SelectedTestInstrument = null;
        SelectedBookEntry = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static ExecutionMetricResult EmptyMetrics() =>
        new(0m, 0m, 0m, 0d, 0m, 0m, 0, 0m, 0, 0);

    private static ExecutionQualityReadModel EmptyQuality() =>
        new(0, 0, 0, 0, 0, 0, 0, 0d, 0, 0d);

    private readonly record struct BrokerCredentialInput(
        string KeyId = "",
        string SecretKey = "",
        string Host = "",
        int Port = 0,
        int ClientId = 0,
        string OAuthClientId = "",
        string OAuthClientSecret = "",
        string OAuthAccessToken = "");

    internal static bool TryParsePositiveScaledPrice(string? text, out ScaledPrice price)
    {
        price = default;
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('+'))
            value = value[1..];
        if (value.Length == 0)
            return false;

        var separator = value.IndexOf('.');
        if (separator != value.LastIndexOf('.'))
            return false;
        var whole = separator < 0 ? value : value[..separator];
        var fraction = separator < 0 ? string.Empty : value[(separator + 1)..];
        if (whole.Length == 0)
            whole = "0";
        if (fraction.Length > 18 ||
            whole.Any(character => !char.IsAsciiDigit(character)) ||
            fraction.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        var digits = $"{whole}{fraction}".TrimStart('0');
        if (digits.Length == 0 ||
            !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient) ||
            coefficient <= 0)
        {
            return false;
        }

        var scale = fraction.Length;
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }
        price = new ScaledPrice(coefficient, (byte)scale);
        return price.IsValid;
    }
}
