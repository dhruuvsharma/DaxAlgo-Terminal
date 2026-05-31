namespace TradingTerminal.Core.Strategies.Parameters;

/// <summary>
/// An ordered, immutable set of <see cref="StrategyParameter"/> declarations — the full
/// "spec sheet" of tunables a strategy exposes. A strategy advertises one of these; the
/// backtest catalog and the live host build editors and runtime values from it.
///
/// Keys must be unique. Use <see cref="CreateDefaults"/> to materialise a runtime value
/// bag seeded with each parameter's default.
/// </summary>
public sealed class StrategyParameterSchema
{
    /// <summary>A schema with no tunables — the default for strategies that take no parameters.</summary>
    public static StrategyParameterSchema Empty { get; } = new(Array.Empty<StrategyParameter>());

    public StrategyParameterSchema(IEnumerable<StrategyParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = parameters.ToArray();

        _byKey = new Dictionary<string, StrategyParameter>(StringComparer.Ordinal);
        foreach (var p in Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key))
                throw new ArgumentException("Parameter key must be non-empty.", nameof(parameters));
            if (!_byKey.TryAdd(p.Key, p))
                throw new ArgumentException($"Duplicate parameter key '{p.Key}'.", nameof(parameters));
        }
    }

    public StrategyParameterSchema(params StrategyParameter[] parameters)
        : this((IEnumerable<StrategyParameter>)parameters) { }

    private readonly Dictionary<string, StrategyParameter> _byKey;

    public IReadOnlyList<StrategyParameter> Parameters { get; }

    public bool IsEmpty => Parameters.Count == 0;

    public StrategyParameter? Find(string key) =>
        _byKey.TryGetValue(key, out var p) ? p : null;

    /// <summary>Builds a fresh value bag with every parameter set to its declared default.</summary>
    public StrategyParameters CreateDefaults() => new(this);
}
