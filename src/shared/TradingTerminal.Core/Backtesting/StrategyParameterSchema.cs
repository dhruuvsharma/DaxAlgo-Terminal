namespace TradingTerminal.Core.Backtesting;

/// <summary>The domain of a tunable parameter — drives both UI controls and how the optimizer sweeps it.</summary>
public enum ParameterKind
{
    Continuous,
    Integer,
    Boolean,
    Categorical,
}

/// <summary>
/// Describes one tunable parameter of a strategy kernel: its identity, default, valid range, and the
/// step the optimizer should walk. One descriptor drives three consumers without divergence — the
/// Studio auto-generates a control from it, the optimizer derives a sweep axis from
/// <see cref="Min"/>/<see cref="Max"/>/<see cref="Step"/>, and the Python seam reads it to validate
/// caller-supplied values.
/// </summary>
public sealed record ParameterDescriptor(
    string Name,
    string Label,
    double Default,
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    double Step = 0,
    ParameterKind Kind = ParameterKind.Continuous,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Clamp a value into this parameter's domain and snap to its kind (integers/booleans round).</summary>
    public double Clamp(double value)
    {
        var v = Math.Clamp(value, Min, Max);
        return Kind is ParameterKind.Integer or ParameterKind.Boolean or ParameterKind.Categorical
            ? Math.Round(v)
            : v;
    }
}

/// <summary>
/// The full tunable surface of a kernel. Lets callers build a valid <see cref="StrategyParameters"/>
/// from defaults (<see cref="Defaults"/>) or by overlaying caller overrides onto defaults with
/// per-parameter clamping (<see cref="Resolve"/>), so an out-of-range or partial parameter set can
/// never reach a kernel.
/// </summary>
public sealed record StrategyParameterSchema(IReadOnlyList<ParameterDescriptor> Parameters)
{
    public static StrategyParameterSchema Empty { get; } = new(Array.Empty<ParameterDescriptor>());

    public ParameterDescriptor? Find(string name) => Parameters.FirstOrDefault(p => p.Name == name);

    public StrategyParameters Defaults() =>
        new(Parameters.ToDictionary(p => p.Name, p => p.Default));

    /// <summary>Overlay <paramref name="overrides"/> onto the defaults, clamping each to its domain.</summary>
    public StrategyParameters Resolve(IReadOnlyDictionary<string, double>? overrides)
    {
        var values = new Dictionary<string, double>();
        foreach (var p in Parameters)
        {
            var raw = overrides is not null && overrides.TryGetValue(p.Name, out var o) ? o : p.Default;
            values[p.Name] = p.Clamp(raw);
        }
        return new StrategyParameters(values);
    }
}
