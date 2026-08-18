namespace TradingTerminal.Core.Accounts;

/// <summary>Where an account sign-in attempt ended up.</summary>
public enum AccountSignInState
{
    /// <summary>No account is signed in.</summary>
    SignedOut = 0,

    /// <summary>A sign-in is in flight; the system browser may be open.</summary>
    Working = 1,

    /// <summary>An account is signed in and entitled.</summary>
    SignedIn = 2,

    /// <summary>The last attempt failed; <see cref="IAccountSignInPanel.StatusMessage"/> says why.</summary>
    Failed = 3,
}

/// <summary>
/// The account sign-in panel shown above the broker forms on the login window.
///
/// <para>This is a <b>seam, not an implementation</b>. The login window is part of the open-source
/// base and must not reference the entitlement layer, but the panel it renders is supplied by the
/// paid editions. A host that registers nothing gets no panel and the login window shows only broker
/// forms — which is exactly right for the open-source edition, where there is no account gate at
/// all.</para>
///
/// <para>Deliberately narrow: it exposes an identity and two actions, and knows nothing about
/// entitlement plans, tokens, or how the sign-in is performed. Everything behind it stays private.</para>
/// </summary>
public interface IAccountSignInPanel
{
    /// <summary>Where the panel currently stands.</summary>
    AccountSignInState State { get; }

    /// <summary>The signed-in account's display identity, or empty when signed out.</summary>
    string AccountLabel { get; }

    /// <summary>A short line for the user: what is happening, or why the last attempt failed.</summary>
    string StatusMessage { get; }

    /// <summary>
    /// Whether to keep the session across restarts. Persisting a session is the host's decision, so
    /// this is only the user's stated preference.
    /// </summary>
    bool RememberMe { get; set; }

    /// <summary>Begins an interactive sign-in. Completes when the attempt settles, successfully or not.</summary>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the current session, returning the panel to <see cref="AccountSignInState.SignedOut"/>.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens account creation. Accounts are created on the platform rather than in the terminal, so
    /// this hands off to the system browser instead of collecting credentials here.
    /// </summary>
    void OpenAccountCreation();

    /// <summary>Raised when any of the above changes, so the login view-model can re-read them.</summary>
    event EventHandler? Changed;
}
