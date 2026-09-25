using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// A stream made by asking a REST endpoint again and again — for brokers whose own stream is either
/// absent or a protocol this adapter does not speak (Fyers' binary socket, ICICI's socket.io).
///
/// <para>Honest about what it is: an item is yielded only when it differs from the last one, so a quiet
/// market produces nothing rather than the same quote a thousand times; a failure backs off rather than
/// hammering a broker that is refusing; and the interval is the broker's rate limit, not a guess.</para>
/// </summary>
internal static class Polling
{
    public static async IAsyncEnumerable<T> PollAsync<T>(
        TimeSpan interval,
        Func<CancellationToken, Task<T?>> fetch,
        ILogger logger,
        string name,
        IEqualityComparer<T>? sameAs = null,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        sameAs ??= EqualityComparer<T>.Default;
        T? last = null;
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            T? item = null;
            try
            {
                item = await fetch(ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                failures++;
                // Warn once, then quietly: a signed-out session fails every poll and one line says so.
                if (failures == 1) logger.LogWarning(ex, "{Name} poll failed; backing off.", name);
                else logger.LogDebug(ex, "{Name} poll failed again ({Failures}).", name, failures);
            }

            if (item is not null && (last is null || !sameAs.Equals(item, last)))
            {
                last = item;
                yield return item;
            }

            var wait = failures == 0 ? interval : TimeSpan.FromSeconds(Math.Min(30, interval.TotalSeconds * Math.Pow(2, failures)));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }
}

/// <summary>
/// India Standard Time, UTC+05:30, without daylight saving — the zone every Indian broker writes its
/// timestamps in when it writes them without an offset.
/// </summary>
internal static class IndiaTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5.5);

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy HH:mm:ss",
        "dd-MMM-yyyy HH:mm:ss", "dd-MMM-yyyy", "yyyy-MM-dd",
    ];

    /// <summary>A broker timestamp as UTC. With an offset (<c>2017-12-15T09:15:00+0530</c>) the offset is
    /// honoured; without one it is read as IST. Null when unparseable.</summary>
    public static DateTime? ToUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        // "+0530" is not an offset .NET parses; "+05:30" is.
        if (text.Length > 5 && (text[^5] is '+' or '-') && text[^4..].All(char.IsAsciiDigit))
            text = text[..^2] + ":" + text[^2..];

        if (text.Contains('T') && (text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            return withOffset.UtcDateTime;

        return DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local - Offset, DateTimeKind.Utc)
            : null;
    }

    /// <summary>A UTC time as IST wall-clock text in <paramref name="format"/>, for request parameters.</summary>
    public static string Format(DateTime utc, string format) =>
        (DateTime.SpecifyKind(utc, DateTimeKind.Utc) + Offset).ToString(format, CultureInfo.InvariantCulture);
}
