namespace TradingTerminal.Core.Brokers;

/// <summary>What a venue said when asked whether a key works.</summary>
/// <param name="Checked">
/// False when no check was possible — no verifier composed, or this broker has no endpoint that can
/// answer. <b>Distinct from a failed check</b>, and the distinction matters: "we could not ask" must
/// never be shown to a user as "your key is wrong".
/// </param>
/// <param name="Ok">True when the venue accepted the credentials.</param>
/// <param name="Detail">The venue's own words when it refused. Empty otherwise.</param>
public readonly record struct CredentialVerification(bool Checked, bool Ok, string Detail = "")
{
    /// <summary>No check was made.</summary>
    public static CredentialVerification NotChecked { get; } = new(false, false);

    /// <summary>The venue accepted the key.</summary>
    public static CredentialVerification Accepted { get; } = new(true, true);

    /// <summary>The venue refused, and said why.</summary>
    public static CredentialVerification Refused(string detail) => new(true, false, detail);

    /// <summary>True only for a check that ran and failed — the one state worth blocking a login on.
    /// An unchecked credential proceeds, because refusing to connect over a question nobody could ask
    /// would make an unreachable network look like a bad key.</summary>
    public bool IsRefusal => Checked && !Ok;
}

/// <summary>
/// Asks a venue whether a credential is good, before anything depends on it.
///
/// <para><b>The problem it solves.</b> On every venue that serves public market data, a wrong key
/// changes nothing observable: the charts fill from the public feed, the login succeeds, and the key
/// sits in the store looking configured until the first private call fails days later with an error
/// about a signature. Verifying at paste time is the only moment the user still has the key on their
/// clipboard and the venue's API page open.</para>
///
/// <para>It lives in Core for the usual reason: the login form that collects a key is in the shell, the
/// code that knows how to sign a request is in the infrastructure layer, and the shell does not
/// reference the infrastructure.</para>
/// </summary>
public interface IBrokerCredentialVerifier
{
    /// <summary>True when this verifier can actually check <paramref name="broker"/>. A form asks
    /// first so it can leave the check out of the flow entirely rather than showing a result that
    /// means nothing.</summary>
    bool CanVerify(BrokerKind broker);

    /// <summary>
    /// Makes the check. <b>Never throws</b> — an unreachable venue is reported as
    /// <see cref="CredentialVerification.NotChecked"/>, not as a rejection, because a network problem
    /// and a bad key are different problems with different fixes.
    /// </summary>
    Task<CredentialVerification> VerifyAsync(
        BrokerKind broker, BrokerCredential credential, CancellationToken ct = default);

    /// <summary>A verifier that checks nothing — what an edition composes when no verifier is wired.
    /// Logins proceed exactly as they did before one existed.</summary>
    public static IBrokerCredentialVerifier None { get; } = new NoVerifier();

    private sealed class NoVerifier : IBrokerCredentialVerifier
    {
        public bool CanVerify(BrokerKind broker) => false;

        public Task<CredentialVerification> VerifyAsync(
            BrokerKind broker, BrokerCredential credential, CancellationToken ct = default) =>
            Task.FromResult(CredentialVerification.NotChecked);
    }
}
