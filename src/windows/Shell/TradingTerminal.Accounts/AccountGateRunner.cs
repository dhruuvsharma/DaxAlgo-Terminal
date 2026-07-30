using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

public static class AccountGateRunner
{
    internal const string PlatformBaseUrlEnvironmentVariable = "DAXALGO_PLATFORM_BASE_URL";
    internal const string PlatformPublicKeyEnvironmentVariable =
        "DAXALGO_PLATFORM_ENTITLEMENT_PUBLIC_KEY";
    internal const string PlatformTimeoutEnvironmentVariable =
        "DAXALGO_PLATFORM_TIMEOUT_SECONDS";

    private static int _forceFreshAuthentication;

    public static bool Show(AppEdition requiredEdition)
    {
        var environmentName = GetEnvironmentName();
        return Show(requiredEdition, environmentName, null, null, null);
    }

    public static bool Show(
        AppEdition requiredEdition,
        GoogleAuthOptions googleAuthOptions)
    {
        ArgumentNullException.ThrowIfNull(googleAuthOptions);
        return Show(
            requiredEdition,
            GetEnvironmentName(),
            null,
            null,
            null,
            googleAuthOptions);
    }

    /// <summary>
    /// Clears the DPAPI-protected local account session and forces the next gate invocation to
    /// authenticate interactively even if the file cannot be removed.
    /// </summary>
    public static bool ClearStoredAccount()
    {
        Volatile.Write(ref _forceFreshAuthentication, 1);
        return DevelopmentAccountSessionStore.CreateDefault().Clear();
    }

    public static bool Show(
        AppEdition requiredEdition,
        string environmentName,
        IAccountAuthenticationService? authentication,
        IEntitlementService? entitlements,
        IAccountGateDiagnostics? diagnostics = null,
        GoogleAuthOptions? googleAuthOptions = null,
        PlatformOptions? platformOptions = null)
    {
        var edition = AccountGateEditionProfile.For(requiredEdition);
        var forceFreshAuthentication =
            Interlocked.Exchange(ref _forceFreshAuthentication, 0) != 0;
        var services = AccountGateServiceFactory.Create(
            environmentName,
            authentication,
            entitlements,
            googleAuthOptions: googleAuthOptions,
            forceFreshAuthentication: forceFreshAuthentication,
            platformOptions: platformOptions ?? GetPlatformOptions());
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            requiredEdition,
            TimeProvider.System,
            diagnostics);
        var viewModel = new AccountGateViewModel(
            coordinator,
            edition,
            services.Mode,
            services.HasGoogleAuthentication);
        var window = new AccountGateWindow(viewModel);
        return window.ShowDialog() == true;
    }

    private static string GetEnvironmentName() =>
        Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    private static PlatformOptions GetPlatformOptions()
    {
        var timeoutValue = Environment.GetEnvironmentVariable(
            PlatformTimeoutEnvironmentVariable);
        var timeoutSeconds = int.TryParse(timeoutValue, out var configuredTimeout)
            ? configuredTimeout
            : PlatformOptions.DefaultTimeoutSeconds;
        return new PlatformOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable(
                PlatformBaseUrlEnvironmentVariable) ?? string.Empty,
            EntitlementLeasePublicKey = Environment.GetEnvironmentVariable(
                PlatformPublicKeyEnvironmentVariable) ?? string.Empty,
            TimeoutSeconds = timeoutSeconds,
        };
    }
}
