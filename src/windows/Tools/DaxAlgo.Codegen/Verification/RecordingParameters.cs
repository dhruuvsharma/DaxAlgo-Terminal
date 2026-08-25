using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Wraps the real parameters and notes which keys the unit asked for.
///
/// <para>Rung 5 could have been done by reading the source, and that was the first idea. It does not
/// survive contact with how these units are actually written: the declaration
/// (<c>StrategyParameter.Int("lookback", …)</c>) and the read
/// (<c>parameters.GetInt("lookback")</c>) both put the same literal in the same file, so finding the
/// string proves nothing, and the samples reference their keys through <c>const</c> fields that the
/// compiler folds away before anything can count them.</para>
///
/// <para>Observing the reads instead is exact, needs no source, and cannot be fooled by how the key
/// was spelled at the call site. It also measures the thing the rule is actually about — whether the
/// value reached the code — rather than a proxy for it.</para>
/// </summary>
public sealed class RecordingParameters(IParameters inner) : IParameters
{
    private readonly IParameters _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    /// <summary>Keys the unit read, in no particular order.</summary>
    public IReadOnlyCollection<string> KeysRead => _read;

    public StrategyParameterSchema Schema => _inner.Schema;

    public bool GetBool(string name) => Record(name, _inner.GetBool);

    public double GetDouble(string name) => Record(name, _inner.GetDouble);

    public TEnum GetEnum<TEnum>(string name) where TEnum : struct, Enum =>
        Record(name, _inner.GetEnum<TEnum>);

    public InstrumentId GetInstrument(string name) => Record(name, _inner.GetInstrument);

    public int GetInt(string name) => Record(name, _inner.GetInt);

    public long GetLong(string name) => Record(name, _inner.GetLong);

    public string GetString(string name) => Record(name, _inner.GetString);

    public string GetText(string name) => Record(name, _inner.GetText);

    /// <summary>Records the key before delegating, so a key that throws — because it was never declared
    /// — is still counted. That read is exactly the fault rung 5 reports.</summary>
    private T Record<T>(string name, Func<string, T> read)
    {
        _read.Add(name);
        return read(name);
    }
}
