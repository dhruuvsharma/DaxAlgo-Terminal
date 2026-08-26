namespace TradingTerminal.Core.Brokers;

/// <summary>
/// One broker's credentials, in the three shapes every broker on the catalogue actually uses.
/// </summary>
/// <param name="Key">
/// The public half: an API key, an account id, a client id, or an environment name. Whatever it is, it
/// is an <b>identifier rather than a secret</b> — which is what lets a login form show which account is
/// configured without decrypting anything.
/// </param>
/// <param name="Secret">The secret half: an API secret, a bearer token, or a private key in PEM.</param>
/// <param name="Passphrase">A third factor, for the venues that issue one. Usually empty.</param>
public readonly record struct BrokerCredential(
    string Key = "",
    string Secret = "",
    string Passphrase = "")
{
    /// <summary>Nothing configured.</summary>
    public static BrokerCredential None { get; }

    /// <summary>True when there is a secret to authenticate with. Some brokers need only that, so the
    /// key is not required — a bearer token has no key half.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    /// <summary>True when both halves are present, which is what a key-and-secret venue needs.</summary>
    public bool IsPair => IsConfigured && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// Where a broker client gets its credentials.
///
/// <para><b>One seam for every broker</b>, rather than an interface per venue. There are thirty-odd
/// brokers on the catalogue and they authenticate in four or five ways between them; a
/// <c>IWhicheverTokenSource</c> for each would be thirty near-identical interfaces, thirty
/// registrations, and thirty chances for one of them to be wired to nothing.</para>
///
/// <para>It lives in Core because both ends need it and they cannot see each other: the login form that
/// collects a key is in the shell, the client that spends it is in the infrastructure layer, and the
/// shell does not reference the infrastructure. The same arrangement <c>IAiKeyResolver</c> uses, for the
/// same reason.</para>
///
/// <para><b>Nothing here reads a file.</b> Implementations sit over the DPAPI credential store; a
/// market-data client has no business knowing where a secret is kept, only that it can ask for one.</para>
/// </summary>
public interface IBrokerCredentialSource
{
    /// <summary>The credentials for <paramref name="broker"/>, or <see cref="BrokerCredential.None"/>
    /// when none are configured. Never throws — an unconfigured broker is an ordinary state, and the
    /// client reports "needs a key" rather than failing to construct.</summary>
    BrokerCredential For(BrokerKind broker);

    /// <summary>A source that never has anything — what an edition composes when no credential store is
    /// wired. Keyless brokers still work; keyed ones say what they are missing.</summary>
    public static IBrokerCredentialSource None { get; } = new NoCredentials();

    private sealed class NoCredentials : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) => BrokerCredential.None;
    }
}
