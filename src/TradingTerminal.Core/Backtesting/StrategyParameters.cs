namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// An immutable name → value bag for strategy parameters. Values are doubles so the same bag
/// drives the optimizer (a parameter axis sweeps a numeric range), the CLI
/// (<c>--param k=v</c>), and the Python authoring seam (JSON numbers) without a type fork.
/// Categorical choices are encoded as enum ordinals; consumers read them via <see cref="GetInt"/>.
///
/// This is the new system's replacement for ad-hoc ctor arguments on strategy classes: a kernel
/// reads what it needs out of <see cref="IStrategyContext.Parameters"/>, which lets one kernel be
/// swept, serialized, and authored in either language without recompilation.
/// </summary>
public sealed class StrategyParameters
{
    private readonly IReadOnlyDictionary<string, double> _values;

    public StrategyParameters(IReadOnlyDictionary<string, double>? values = null)
        => _values = values ?? new Dictionary<string, double>();

    public static StrategyParameters Empty { get; } = new();

    /// <summary>Reads a required parameter; throws <see cref="KeyNotFoundException"/> if absent.</summary>
    public double this[string key] => _values[key];

    public bool Contains(string key) => _values.ContainsKey(key);

    public double GetOr(string key, double fallback) =>
        _values.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var v) ? (int)Math.Round(v) : fallback;

    public bool GetBool(string key, bool fallback) =>
        _values.TryGetValue(key, out var v) ? v != 0 : fallback;

    /// <summary>Returns a new bag with <paramref name="key"/> set — the original is unchanged.</summary>
    public StrategyParameters With(string key, double value)
    {
        var next = new Dictionary<string, double>(_values) { [key] = value };
        return new StrategyParameters(next);
    }

    public IReadOnlyDictionary<string, double> Values => _values;
}
