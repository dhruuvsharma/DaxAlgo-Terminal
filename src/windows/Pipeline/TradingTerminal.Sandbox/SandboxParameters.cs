using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Sandbox;

/// <summary>
/// A read-only, kind-checked view over host-owned strategy parameter values. Parameter names are
/// ordinal and case-sensitive, matching <see cref="StrategyParameterSchema"/>.
/// </summary>
public sealed class SandboxParameters : IParameters
{
    private readonly StrategyParameters _values;

    public SandboxParameters(
        StrategyParameterSchema schema,
        IReadOnlyDictionary<string, object?>? currentValues = null)
        : this(new StrategyParameters(schema, currentValues))
    {
    }

    /// <summary>
    /// Wraps a host-owned value bag. Host changes remain visible, while sandbox code receives only
    /// the read-only <see cref="IParameters"/> surface.
    /// </summary>
    public SandboxParameters(StrategyParameters currentValues)
    {
        ArgumentNullException.ThrowIfNull(currentValues);
        _values = currentValues;
    }

    public StrategyParameterSchema Schema => _values.Schema;

    public int GetInt(string name)
    {
        RequireKind(name, ParameterKind.Integer, nameof(GetInt));
        return checked((int)_values.GetLong(name));
    }

    public long GetLong(string name)
    {
        RequireKind(name, ParameterKind.Integer, nameof(GetLong));
        return _values.GetLong(name);
    }

    public double GetDouble(string name)
    {
        RequireKind(name, ParameterKind.Number, nameof(GetDouble));
        return _values.GetDouble(name);
    }

    public bool GetBool(string name)
    {
        RequireKind(name, ParameterKind.Boolean, nameof(GetBool));
        return _values.GetBool(name);
    }

    public string GetString(string name)
    {
        var parameter = Require(name);
        if (parameter.Kind is not (ParameterKind.Choice or ParameterKind.Text))
            ThrowWrongKind(parameter, "Choice or Text", nameof(GetString));

        return _values.GetString(name);
    }

    public string GetText(string name)
    {
        RequireKind(name, ParameterKind.Text, nameof(GetText));
        return _values.GetText(name);
    }

    public TEnum GetEnum<TEnum>(string name) where TEnum : struct, Enum
    {
        RequireKind(name, ParameterKind.Choice, nameof(GetEnum));
        return _values.GetEnum<TEnum>(name);
    }

    public InstrumentId GetInstrument(string name)
    {
        RequireKind(name, ParameterKind.Instrument, nameof(GetInstrument));
        return _values.GetInstrument(name);
    }

    private StrategyParameter Require(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Schema.Find(name)
            ?? throw new KeyNotFoundException($"No parameter '{name}' exists in the supplied schema.");
    }

    private void RequireKind(string name, ParameterKind expected, string accessor)
    {
        var parameter = Require(name);
        if (parameter.Kind != expected)
            ThrowWrongKind(parameter, expected.ToString(), accessor);
    }

    private static void ThrowWrongKind(StrategyParameter parameter, string expected, string accessor) =>
        throw new InvalidOperationException(
            $"Parameter '{parameter.Key}' is declared as {parameter.Kind}; {accessor} requires {expected}.");
}
