using System.Globalization;

namespace TradingTerminal.Backtest.Cli;

/// <summary>
/// Tiny CLI parser: supports <c>--name value</c> and <c>--name=value</c>. No NuGet dependency
/// on System.CommandLine — the surface is small enough to roll our own and avoid pulling a
/// preview package into the repo.
/// </summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = a[2..];
            string val;
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                val = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                val = argv[++i];
            }
            else
            {
                val = "true";
            }
            _values[key] = val;
        }
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var v) ? v
            : throw new ArgumentException($"Missing required argument: --{name}");

    public string? Optional(string name) =>
        _values.TryGetValue(name, out var v) ? v : null;

    public double Double(string name, double @default) =>
        _values.TryGetValue(name, out var v)
            ? double.Parse(v, CultureInfo.InvariantCulture)
            : @default;

    public int Int(string name, int @default) =>
        _values.TryGetValue(name, out var v)
            ? int.Parse(v, CultureInfo.InvariantCulture)
            : @default;

    public DateTime? Date(string name)
    {
        if (!_values.TryGetValue(name, out var v)) return null;
        return DateTime.SpecifyKind(
            DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DateTimeKind.Utc);
    }
}
