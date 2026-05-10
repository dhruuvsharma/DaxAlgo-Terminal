namespace TradingTerminal.Core.Notifications;

public enum NotificationKind
{
    /// <summary>An armed-and-fired signal — the strategy would (or did) act on it.</summary>
    Signal,

    /// <summary>A signal detected while the algo is idle. Useful for tuning, noisy by design.</summary>
    IdleSignal,

    /// <summary>The user armed the algo.</summary>
    AlgoArmed,

    /// <summary>The user disarmed the algo.</summary>
    AlgoStopped,

    /// <summary>An order or fill, once strategies start placing real orders.</summary>
    Trade,

    /// <summary>Sent from the Settings tab to verify the channel works.</summary>
    Test,
}
