using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>
/// Default <see cref="IBrokerLoginFormFactory"/>. Each per-broker form is registered in DI as
/// both its concrete type and as <see cref="IBrokerLoginForm"/>; this factory just looks them
/// up by <see cref="IBrokerLoginForm.Broker"/> and exposes the full set for tile rendering.
/// </summary>
public sealed class BrokerLoginFormFactory : IBrokerLoginFormFactory
{
    private readonly IReadOnlyDictionary<BrokerKind, IBrokerLoginForm> _byBroker;

    public BrokerLoginFormFactory(IEnumerable<IBrokerLoginForm> forms, IBrokerSelector selector)
    {
        // Only expose forms for brokers whose real client was actually registered.
        var available = forms.Where(f => selector.IsAvailable(f.Broker)).ToArray();

        // A broker may contribute MORE THAN ONE form. The five crypto venues that serve data both
        // publicly and with a key have two rows each, sharing a BrokerKind because they share a client
        // and a market. Keying this dictionary directly threw on the duplicate.
        //
        // Get() answers with the first, which is the keyless row — the right default for a lookup by
        // broker, since it is the one that needs nothing from the user. Callers wanting a specific row
        // enumerate All.
        _byBroker = available
            .GroupBy(f => f.Broker)
            .ToDictionary(group => group.Key, group => group.First());

        All = available
            .OrderBy(f => (int)f.Broker)
            .ThenBy(f => f.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<IBrokerLoginForm> All { get; }

    public IBrokerLoginForm Get(BrokerKind kind)
    {
        if (!_byBroker.TryGetValue(kind, out var form))
            throw new InvalidOperationException(
                $"No login form registered for broker {kind} — its SDK was not present at build time.");
        return form;
    }
}
