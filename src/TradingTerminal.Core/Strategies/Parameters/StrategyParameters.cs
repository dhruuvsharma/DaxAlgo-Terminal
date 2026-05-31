using System.Globalization;

namespace TradingTerminal.Core.Strategies.Parameters;

/// <summary>
/// Runtime values for a <see cref="StrategyParameterSchema"/>. Coerces, validates, and
/// clamps each value against its declaration so strategy code can read strongly-typed
/// settings with <c>GetInt</c>/<c>GetDouble</c>/<c>GetBool</c>/<c>GetString</c> and never
/// see an out-of-range or wrong-typed value.
///
/// Values arrive from three places — defaults, the UI editor, and persisted JSON — so
/// every setter funnels through <see cref="Coerce"/>, which is tolerant of the boxed type
/// (an int set as <c>double</c>, a bool set as the string "true", a JSON number, etc.).
/// </summary>
public sealed class StrategyParameters
{
    public StrategyParameters(StrategyParameterSchema schema, IReadOnlyDictionary<string, object?>? values = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = schema;
        _values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var p in schema.Parameters)
            _values[p.Key] = Coerce(p, p.Default);

        if (values is null) return;
        foreach (var (key, value) in values)
        {
            // Ignore unknown keys (forward-compat with renamed/removed params in saved presets).
            if (schema.Find(key) is { } param)
                _values[key] = Coerce(param, value);
        }
    }

    private readonly Dictionary<string, object?> _values;

    public StrategyParameterSchema Schema { get; }

    /// <summary>Sets a value, coercing and clamping it against the parameter declaration.</summary>
    public void Set(string key, object? value)
    {
        var param = Require(key);
        _values[key] = Coerce(param, value);
    }

    public int GetInt(string key) => (int)GetLong(key);

    public long GetLong(string key) =>
        _values[Require(key).Key] is long l ? l : Convert.ToInt64(_values[key], CultureInfo.InvariantCulture);

    public double GetDouble(string key) =>
        _values[Require(key).Key] is double d ? d : Convert.ToDouble(_values[key], CultureInfo.InvariantCulture);

    public bool GetBool(string key) =>
        _values[Require(key).Key] is bool b ? b : Convert.ToBoolean(_values[key], CultureInfo.InvariantCulture);

    public string GetString(string key) =>
        _values[Require(key).Key]?.ToString() ?? string.Empty;

    /// <summary>Raw boxed value, of the natural CLR type for the parameter's kind.</summary>
    public object? GetRaw(string key) => _values[Require(key).Key];

    /// <summary>Snapshot suitable for persistence (e.g. JSON serialisation of a preset).</summary>
    public IReadOnlyDictionary<string, object?> ToDictionary() =>
        new Dictionary<string, object?>(_values, StringComparer.Ordinal);

    /// <summary>
    /// Returns human-readable problems (out-of-range numbers, choices not in the allowed
    /// set). Coercion already clamps on <see cref="Set"/>, so a freshly-built bag is valid;
    /// this is for surfacing imported/edited values that fell outside bounds.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var p in Schema.Parameters)
        {
            var v = _values[p.Key];
            switch (p.Kind)
            {
                case ParameterKind.Integer or ParameterKind.Number:
                    var num = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                    if (p.Min is { } min && num < min)
                        errors.Add($"{p.DisplayName} ({num}) is below the minimum {min}.");
                    if (p.Max is { } max && num > max)
                        errors.Add($"{p.DisplayName} ({num}) is above the maximum {max}.");
                    break;
                case ParameterKind.Choice when p.Choices is { } choices:
                    var s = v?.ToString();
                    if (s is null || !choices.Contains(s, StringComparer.Ordinal))
                        errors.Add($"{p.DisplayName} ('{s}') is not one of: {string.Join(", ", choices)}.");
                    break;
            }
        }
        return errors;
    }

    private StrategyParameter Require(string key) =>
        Schema.Find(key) ?? throw new KeyNotFoundException($"No parameter '{key}' in schema.");

    private static object? Coerce(StrategyParameter p, object? value) => p.Kind switch
    {
        ParameterKind.Integer => Clamp(p, ToLong(value, p.Default)),
        ParameterKind.Number => Clamp(p, ToDouble(value, p.Default)),
        ParameterKind.Boolean => ToBool(value, p.Default),
        ParameterKind.Choice => ToChoice(p, value),
        ParameterKind.Text => value?.ToString() ?? (p.Default?.ToString() ?? string.Empty),
        _ => value,
    };

    private static long Clamp(StrategyParameter p, long v)
    {
        if (p.Min is { } min && v < min) v = (long)Math.Ceiling(min);
        if (p.Max is { } max && v > max) v = (long)Math.Floor(max);
        return v;
    }

    private static double Clamp(StrategyParameter p, double v)
    {
        if (p.Min is { } min && v < min) v = min;
        if (p.Max is { } max && v > max) v = max;
        return v;
    }

    private static long ToLong(object? value, object? fallback) => value switch
    {
        null => Convert.ToInt64(fallback ?? 0L, CultureInfo.InvariantCulture),
        long l => l,
        string s when long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) => r,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => (long)Math.Round(d),
        IConvertible => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(fallback ?? 0L, CultureInfo.InvariantCulture),
    };

    private static double ToDouble(object? value, object? fallback) => value switch
    {
        null => Convert.ToDouble(fallback ?? 0d, CultureInfo.InvariantCulture),
        double d => d,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) => r,
        IConvertible => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(fallback ?? 0d, CultureInfo.InvariantCulture),
    };

    private static bool ToBool(object? value, object? fallback) => value switch
    {
        null => fallback is true,
        bool b => b,
        string s when bool.TryParse(s, out var r) => r,
        string s => s is "1" or "yes" or "on",
        IConvertible => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
        _ => fallback is true,
    };

    private static string ToChoice(StrategyParameter p, object? value)
    {
        var s = value?.ToString();
        if (s is not null && p.Choices is { } choices && choices.Contains(s, StringComparer.Ordinal))
            return s;
        // Fall back to the declared default, else the first allowed choice, else empty.
        return p.Default?.ToString()
            ?? (p.Choices is { Count: > 0 } c ? c[0] : string.Empty);
    }
}
