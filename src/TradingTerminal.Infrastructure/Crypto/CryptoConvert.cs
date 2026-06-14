using System.Globalization;
using System.Text.Json;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>Shared numeric/size helpers for the crypto backends (mirrors the Binance client's privates).</summary>
internal static class CryptoConvert
{
    /// <summary>Scale a fractional crypto quantity to the integer canonical size field.</summary>
    public static long ToSize(double qty, double scale) => (long)Math.Round(qty * scale);

    /// <summary>Parse a JSON number-or-string into a double (exchanges send numbers as strings).</summary>
    public static double D(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
        JsonValueKind.Number => el.GetDouble(),
        _ => 0,
    };

    /// <summary>Parse property <paramref name="name"/> off <paramref name="obj"/> as a double, or 0 if absent.</summary>
    public static double D(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? D(e) : 0;

    public static long MsToTicksUtc(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
            if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s)) return s;
        }
        return 0;
    }
}
