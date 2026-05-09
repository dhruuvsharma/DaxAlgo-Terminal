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
        _byBroker = forms
            .Where(f => selector.IsAvailable(f.Broker))
            .ToDictionary(f => f.Broker);
        All = _byBroker.Values.OrderBy(f => (int)f.Broker).ToArray();
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
