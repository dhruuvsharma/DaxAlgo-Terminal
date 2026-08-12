using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>
/// Records at most one latest target per declared instrument for a single kernel pass. Submissions
/// outside the declared set are ignored, so the retained dictionary can never grow beyond that set.
/// </summary>
public sealed class RecordingVirtualBook : IVirtualBook
{
    private readonly HashSet<InstrumentId> _declaredInstruments;
    private readonly Dictionary<InstrumentId, VirtualTargetIntent> _recordedIntents;

    /// <summary>Creates a bounded recorder for the complete declared instrument set.</summary>
    public RecordingVirtualBook(IReadOnlySet<InstrumentId> declaredInstruments)
    {
        ArgumentNullException.ThrowIfNull(declaredInstruments);

        _declaredInstruments = new HashSet<InstrumentId>(declaredInstruments);
        _recordedIntents = new Dictionary<InstrumentId, VirtualTargetIntent>(
            _declaredInstruments.Count);
    }

    /// <summary>The latest accepted intent for each instrument in the current pass.</summary>
    public IReadOnlyCollection<VirtualTargetIntent> RecordedIntents => _recordedIntents.Values;

    /// <inheritdoc />
    public void SubmitTarget(VirtualTargetIntent intent)
    {
        if (intent is null || !_declaredInstruments.Contains(intent.Instrument))
            return;

        _recordedIntents[intent.Instrument] = intent;
    }

    /// <summary>Clears the current pass while retaining the bounded dictionary capacity.</summary>
    public void Reset() => _recordedIntents.Clear();

    internal bool TryGetTarget(InstrumentId instrument, out VirtualTargetIntent? intent) =>
        _recordedIntents.TryGetValue(instrument, out intent);
}
