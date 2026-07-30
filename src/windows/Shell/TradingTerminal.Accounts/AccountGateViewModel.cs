using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.UI;

namespace TradingTerminal.Accounts;

internal sealed partial class AccountGateViewModel : ViewModelBase, IDisposable
{
    private readonly AccountGateCoordinator _coordinator;
    private readonly bool _hasGoogleAuthentication;
    private CancellationTokenSource? _activeAttempt;
    private bool _isClosed;
    private bool _isDisposed;

    public AccountGateViewModel(
        AccountGateCoordinator coordinator,
        AccountGateEditionProfile edition,
        AccountGateProviderMode providerMode,
        bool hasGoogleAuthentication = false)
    {
        _coordinator = coordinator;
        _hasGoogleAuthentication = hasGoogleAuthentication;
        PlanName = edition.PlanName;
        PlanPrice = edition.Price;
        PlanSummary = edition.Summary;
        EnvironmentNotice = providerMode == AccountGateProviderMode.Development &&
                            hasGoogleAuthentication
            ? "Google verifies your identity in the system browser. OAuth tokens are discarded; only a DPAPI-protected local development session is retained."
            : providerMode == AccountGateProviderMode.Development
            ? "Development mode uses an in-memory local account. It stores no password or token and is disabled in Release builds."
            : string.Empty;
        HasEnvironmentNotice = EnvironmentNotice.Length > 0;
        HasLocalDeveloperAccess =
            providerMode == AccountGateProviderMode.Development &&
            hasGoogleAuthentication;

        if (HasLocalDeveloperAccess)
        {
            _statusTitle = "Sign in with Google";
            _statusMessage =
                "Continue in your system browser, then return here after Google confirms your identity.";
            _primaryActionText = "Sign in with Google";
        }
        else if (providerMode == AccountGateProviderMode.Development)
        {
            _statusTitle = "Local developer access";
            _statusMessage =
                "Continue with the temporary local developer profile. No external account service is contacted.";
            _primaryActionText = "Continue as local developer";
        }
    }

    public string PlanName { get; }

    public string PlanPrice { get; }

    public string PlanSummary { get; }

    public string EnvironmentNotice { get; }

    public bool HasEnvironmentNotice { get; }

    public bool HasLocalDeveloperAccess { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueAsLocalDeveloperCommand))]
    [NotifyCanExecuteChangedFor(nameof(UseAnotherAccountCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusTitle = "Sign in to continue";

    [ObservableProperty]
    private string _statusMessage =
        "Use the same DaxAlgo account for the desktop terminal and marketplace. " +
        "Sign in and account creation use one secure provider action.";

    [ObservableProperty]
    private string _primaryActionText = "Sign in or create account";

    [ObservableProperty]
    private bool _hasFailure;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseAnotherAccountCommand))]
    private bool _canUseAnotherAccount;

    public event Action<bool>? Completed;

    private bool CanContinue() => !IsBusy && !_isClosed;

    private bool CanSwitchAccount() => CanUseAnotherAccount && !IsBusy && !_isClosed;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsync() => RunAccessAttemptAsync(useLocalDevelopmentAccount: false);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task ContinueAsLocalDeveloperAsync() =>
        RunAccessAttemptAsync(useLocalDevelopmentAccount: true);

    private async Task RunAccessAttemptAsync(bool useLocalDevelopmentAccount)
    {
        if (!CanContinue()) return;

        var attemptCts = BeginAttempt();
        IsBusy = true;
        HasFailure = false;
        CanUseAnotherAccount = false;
        StatusTitle = useLocalDevelopmentAccount
            ? "Starting local developer access"
            : "Checking account";
        StatusMessage = useLocalDevelopmentAccount
            ? "Creating the temporary local development session."
            : "Complete sign-in in the system browser if one opens.";

        try
        {
            var attempt = useLocalDevelopmentAccount
                ? await _coordinator.AcquireLocalDevelopmentAccessAsync(attemptCts.Token)
                : await _coordinator.AcquireAccessAsync(attemptCts.Token);
            if (!IsCurrent(attemptCts)) return;

            if (attempt.IsGranted)
            {
                Complete(true);
                return;
            }

            HasFailure = true;
            CanUseAnotherAccount = attempt.Failure == AccountGateAttemptFailure.EntitlementDenied;
            PrimaryActionText = _hasGoogleAuthentication
                ? "Retry Google sign-in"
                : "Retry";
            StatusTitle = attempt.Failure == AccountGateAttemptFailure.EntitlementDenied
                ? "Plan access required"
                : "Account access unavailable";
            StatusMessage = attempt.FailureMessage ?? "Account access could not be verified. Retry or cancel.";
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsCurrent(attemptCts)) return;
            HasFailure = true;
            CanUseAnotherAccount = false;
            PrimaryActionText = "Retry";
            StatusTitle = "Account check failed";
            StatusMessage = "Account access could not be verified. Check your connection and retry.";
        }
        finally
        {
            EndAttempt(attemptCts);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchAccount))]
    private async Task UseAnotherAccountAsync()
    {
        if (!CanSwitchAccount()) return;

        CancelActiveAttempt();
        var attemptCts = BeginAttempt();
        IsBusy = true;
        StatusTitle = "Signing out";
        StatusMessage = "Preparing a fresh account sign-in.";

        try
        {
            var result = await _coordinator.SignOutAsync(attemptCts.Token);
            if (!IsCurrent(attemptCts)) return;

            if (result.IsSuccessful)
            {
                HasFailure = false;
                CanUseAnotherAccount = false;
                PrimaryActionText = _hasGoogleAuthentication
                    ? "Sign in with Google"
                    : "Sign in or create account";
                StatusTitle = "Choose another account";
                StatusMessage = "The previous account was signed out. Continue to sign in or create another account.";
                return;
            }

            HasFailure = true;
            CanUseAnotherAccount = true;
            StatusTitle = "Account switch unavailable";
            StatusMessage = result.FailureMessage ?? "The current account could not be signed out. Retry or cancel.";
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
        {
        }
        catch
        {
            if (!IsCurrent(attemptCts)) return;
            HasFailure = true;
            CanUseAnotherAccount = true;
            StatusTitle = "Account switch unavailable";
            StatusMessage = "The current account could not be signed out. Retry or cancel.";
        }
        finally
        {
            EndAttempt(attemptCts);
        }
    }

    [RelayCommand]
    private void Cancel() => Complete(false);

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isClosed = true;
        var attempt = _activeAttempt;
        _activeAttempt = null;
        attempt?.Cancel();
        attempt?.Dispose();
    }

    private CancellationTokenSource BeginAttempt()
    {
        CancelActiveAttempt();
        var attempt = new CancellationTokenSource();
        _activeAttempt = attempt;
        return attempt;
    }

    private bool IsCurrent(CancellationTokenSource attempt) =>
        !_isClosed &&
        !attempt.IsCancellationRequested &&
        ReferenceEquals(_activeAttempt, attempt);

    private void EndAttempt(CancellationTokenSource attempt)
    {
        if (ReferenceEquals(_activeAttempt, attempt))
        {
            _activeAttempt = null;
            if (!_isClosed) IsBusy = false;
        }

        attempt.Dispose();
    }

    private void CancelActiveAttempt() => _activeAttempt?.Cancel();

    private void Complete(bool granted)
    {
        if (_isClosed) return;
        _isClosed = true;
        CancelActiveAttempt();
        Completed?.Invoke(granted);
    }
}
