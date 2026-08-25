using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Keeps every target a strategy submitted, so what it <i>did</i> can be examined rather than inferred.
///
/// <para>The book is the only output a strategy has, which makes this the complete record of its
/// behaviour. Nothing else needs intercepting: there is no router to stub, no fill to simulate, no
/// account to fake — a design decision that pays off here as much as it does in the product.</para>
/// </summary>
public sealed class RecordingVirtualBook : IVirtualBook
{
    private readonly List<VirtualTargetIntent> _intents = [];

    /// <summary>Every target submitted, in order.</summary>
    public IReadOnlyList<VirtualTargetIntent> Intents => _intents;

    public void SubmitTarget(VirtualTargetIntent intent) => _intents.Add(intent);
}
