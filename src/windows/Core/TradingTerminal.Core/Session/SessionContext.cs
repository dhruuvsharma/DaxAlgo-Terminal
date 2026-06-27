namespace TradingTerminal.Core.Session;

/// <summary>
/// Mutable singleton populated by the login flow once the user is authenticated.
/// Anything in the shell can read it to render "signed in as ..." indicators.
/// </summary>
public sealed class SessionContext
{
    public string? Username { get; private set; }
    public string AccountType { get; private set; } = "Paper";
    public DateTime? SignedInAtUtc { get; private set; }
    public bool IsAuthenticated { get; private set; }

    public event EventHandler? Changed;

    public void SetSignedIn(string? username, string accountType)
    {
        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        AccountType = accountType;
        SignedInAtUtc = DateTime.UtcNow;
        IsAuthenticated = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Username = null;
        SignedInAtUtc = null;
        IsAuthenticated = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
