namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Per-broker login-form contract. Each broker contributes an implementation
/// (an MVVM view-model with its own UserControl view) that knows:
///
///   - how to render its broker-specific fields,
///   - how to validate them (<see cref="CanSubmit"/>),
///   - how to push the validated values into the broker's <c>IOptions</c>,
///   - what label / error copy to surface for that broker.
///
/// The shell <c>LoginViewModel</c> stays broker-agnostic — it just orchestrates
/// the active form, broker-tile selection, and the connect flow.
/// </summary>
public interface IBrokerLoginForm
{
    BrokerKind Broker { get; }
    string DisplayName { get; }

    /// <summary>True when all required fields are populated and the user may submit.</summary>
    bool CanSubmit { get; }

    /// <summary>
    /// Pushes the form's current values into the broker's options instance so the
    /// real client picks them up on the next <c>ConnectAsync</c>. Allowed to mutate
    /// shared <c>IOptions&lt;XxxOptions&gt;</c>; not allowed to do I/O.
    /// </summary>
    void ApplyToOptions();

    /// <summary>Account label written into <c>SessionContext.AccountType</c> on success.</summary>
    string GetSessionAccountLabel();

    /// <summary>Surface in the error banner when the connect attempt times out.</summary>
    string GetTimeoutErrorMessage();

    /// <summary>Surface in the error banner when the underlying client reports Failed.</summary>
    string GetFailureMessage();

    /// <summary>Optional load hook — called when the form is shown so it can hydrate from persisted credentials.</summary>
    void Load();

    /// <summary>Optional save hook — called after a successful connect so the form can persist its state.</summary>
    void Save();
}
