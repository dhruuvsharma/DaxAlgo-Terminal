namespace TradingTerminal.Strategies.SignalHosts;

/// <summary>
/// Builds fresh <see cref="SignalGeneratorRouter"/> instances. One per Start press — the
/// router carries per-session state (last tick context, broker-id sequence), so each
/// strategy session needs its own. The factory exists so view-models don't <c>new</c>
/// the concrete type directly, keeping the test seam clean.
/// </summary>
public interface ISignalGeneratorRouterFactory
{
    SignalGeneratorRouter Create();
}

/// <summary>Default impl — vanilla <c>new()</c>. Replaceable in tests via a mock.</summary>
internal sealed class SignalGeneratorRouterFactory : ISignalGeneratorRouterFactory
{
    public SignalGeneratorRouter Create() => new();
}
