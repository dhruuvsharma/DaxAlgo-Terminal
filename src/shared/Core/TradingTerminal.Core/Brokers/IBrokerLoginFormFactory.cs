namespace TradingTerminal.Core.Brokers;

/// <summary>
/// Factory over the per-broker login forms registered in DI. The login window
/// queries this for tile rendering (<see cref="All"/>) and to look up a specific
/// form when the user clicks a broker tile (<see cref="Get"/>).
/// </summary>
public interface IBrokerLoginFormFactory
{
    /// <summary>Every form whose broker is currently available (i.e. its SDK was present at build time).</summary>
    IReadOnlyList<IBrokerLoginForm> All { get; }

    /// <summary>Resolve the form for a specific broker. Throws if the broker isn't available.</summary>
    IBrokerLoginForm Get(BrokerKind kind);
}
