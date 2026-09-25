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

    /// <summary>A JSON number-or-string as a whole number, or 0. Fractional input is truncated —
    /// Gate.io writes millisecond times as <c>"1790274249452.965000"</c>.</summary>
    public static long L(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetInt64(out var n) => n,
        JsonValueKind.Number => (long)el.GetDouble(),
        JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
        JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => (long)d,
        _ => 0,
    };

    /// <summary>
    /// A Unix timestamp as UTC, in whatever unit it arrived — or now when absent, because a missing
    /// venue timestamp should not put an event at 1970.
    ///
    /// <para><b>The unit is read from the magnitude.</b> Venues disagree even within one API: Bithumb
    /// stamps its trades in milliseconds and its order book in microseconds, Bitvavo its book in
    /// nanoseconds. A present-day time is about 1.8 × 10⁹ in seconds and a thousand times more for each
    /// finer unit, so the four ranges cannot overlap for any date between 1973 and 5138. Guessing wrong
    /// used to throw, and a throw drops the whole frame — Bithumb's book never produced a tick until
    /// this read the unit instead of assuming it.</para>
    /// </summary>
    public static DateTime MsUtc(long epoch) => epoch switch
    {
        <= 0 => DateTime.UtcNow,
        < 100_000_000_000 => DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime,
        < 100_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime,
        < 100_000_000_000_000_000 => DateTime.UnixEpoch.AddTicks(epoch * 10),
        _ => DateTime.UnixEpoch.AddTicks(epoch / 100),
    };

    /// <summary>Property <paramref name="name"/> read as Unix milliseconds, UTC.</summary>
    public static DateTime MsUtc(JsonElement obj, string name) =>
        MsUtc(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? L(e) : 0);

    /// <summary>Unix seconds as UTC.</summary>
    public static DateTime SecondsUtc(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;

    /// <summary>Applies <c>[[price, size, …], …]</c> rows to one side of a book. Size zero removes the
    /// level, which is how every venue here spells a deletion.</summary>
    public static void ApplyLevels(L2OrderBook book, JsonElement rows, bool isBid)
    {
        if (rows.ValueKind != JsonValueKind.Array) return;
        foreach (var level in rows.EnumerateArray())
            if (level.ValueKind == JsonValueKind.Array && level.GetArrayLength() >= 2)
                book.Apply(isBid, D(level[0]), D(level[1]));
    }

    /// <summary><see cref="ApplyLevels(L2OrderBook, JsonElement, bool)"/> for a named property, when present.</summary>
    public static void ApplyLevels(L2OrderBook book, JsonElement obj, string name, bool isBid)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var rows))
            ApplyLevels(book, rows, isBid);
    }
}
