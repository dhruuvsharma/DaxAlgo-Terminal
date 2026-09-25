using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// A <see cref="RestBrokerClient{TOptions}"/> whose session is a short-lived access token kept fresh by a
/// <see cref="SessionKeeper"/> — the US and global brokers, whose tokens last minutes (Schwab 30,
/// TradeStation 20, tastytrade 15) rather than the Indian brokers' day.
///
/// <para>Every request asks the keeper for the session, so a token is renewed before it expires and never
/// in the middle of one; a 401 anyway (a token revoked from the broker's side) marks it spent and the
/// request is retried once on a renewed one.</para>
/// </summary>
internal abstract class KeptSessionClient<TOptions> : RestBrokerClient<TOptions> where TOptions : SessionBrokerOptions
{
    protected KeptSessionClient(
        ILogger logger, TOptions options, IBrokerCredentialSource credentials, IBrokerSessionStore store, BrokerKind kind)
        : base(logger, options, credentials)
    {
        Keeper = SessionKeeper.Shared(kind, credentials, store, RenewAsync, logger);
    }

    protected SessionKeeper Keeper { get; }

    /// <summary>Renews <paramref name="session"/>: a refresh-token grant, or a fresh sign-in with the stored
    /// credentials. Throws with the broker's words when refused.</summary>
    protected abstract Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct);

    /// <summary>Puts the session on a request. A bearer token by default.</summary>
    protected virtual void Authorize(HttpRequestMessage request, KeptSession session) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

    /// <summary>Sends an authorised request, renewing and retrying once on a 401.</summary>
    protected async Task<(JsonDocument Doc, string Body)> SendAsync(Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var session = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendJsonAsync(() => Build(build, session), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            Keeper.Invalidate(session);
            var renewed = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
            return await SendJsonAsync(() => Build(build, renewed), ct).ConfigureAwait(false);
        }
    }

    /// <summary>An authorised GET of <paramref name="url"/>.</summary>
    protected Task<(JsonDocument Doc, string Body)> GetAsync(string url, CancellationToken ct) =>
        SendAsync(_ => new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary>
    /// The objects of an HTTP streaming endpoint — a response that never ends, carrying one JSON object
    /// after another (TradeStation). Reconnects with backoff when the stream ends or fails, and renews the
    /// session on a 401.
    /// </summary>
    protected async IAsyncEnumerable<string> StreamObjectsAsync(
        string url, string what, string accept, [EnumeratorCancellation] CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, Options.ReconnectInitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, Options.ReconnectMaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            HttpResponseMessage? response = null;
            Stream? stream = null;
            try
            {
                (response, stream) = await OpenStreamAsync(url, accept, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Broker} {What} stream could not open; retrying in {Delay}s.", BrokerName, what, delay.TotalSeconds);
            }

            if (response is not null && stream is not null)
            {
                using (response)
                using (stream)
                {
                    var splitter = new JsonObjectSplitter();
                    var buffer = new byte[16 * 1024];
                    while (true)
                    {
                        IReadOnlyList<string> objects;
                        try
                        {
                            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                            if (read == 0) break;
                            objects = splitter.Push(buffer.AsSpan(0, read));
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            yield break;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "{Broker} {What} stream dropped; reconnecting.", BrokerName, what);
                            break;
                        }

                        delay = TimeSpan.FromSeconds(Math.Max(1, Options.ReconnectInitialDelaySeconds));
                        foreach (var obj in objects) yield return obj;
                    }
                }
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

    private async Task<(HttpResponseMessage, Stream)> OpenStreamAsync(string url, string accept, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var session = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
            using var request = Build(_ => new HttpRequestMessage(HttpMethod.Get, url), session);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return (response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    Keeper.Invalidate(session);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"{BrokerName} answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}", null, response.StatusCode);
            }
        }
    }

    private HttpRequestMessage Build(Func<KeptSession, HttpRequestMessage> build, KeptSession session)
    {
        var request = build(session);
        Authorize(request, session);
        return request;
    }
}

/// <summary>One polled quote with the fields bars can be built from: last trade and day volume where the
/// broker gives them.</summary>
internal sealed record PolledQuote(DateTime TimeUtc, double Bid, double Ask, long BidSize, long AskSize, double Last = 0, double DayVolume = 0)
{
    /// <summary>The price a bar is built from: the last trade, or the mid when there is none (FX, CFDs,
    /// Robinhood's spread-inclusive quotes).</summary>
    public double Price => Last > 0 ? Last : Bid > 0 && Ask > 0 ? (Bid + Ask) / 2 : 0;

    public Tick? ToTick() => Bid > 0 && Ask > 0 ? new Tick(TimeUtc, Bid, Ask, BidSize, AskSize) : null;
}

/// <summary>
/// Live bars built from polled quotes, for brokers with no candle stream to re-read (E*TRADE has no bar
/// history at all; IG charges each history point against a weekly allowance).
///
/// <para><b>Approximate, and says so in its name.</b> A bar made from quotes sampled every second or two
/// misses any high or low that came and went between samples, and its volume is the difference in the day's
/// cumulative volume between samples — zero where the broker publishes none. It is the forming bar a chart
/// needs, not a substitute for the broker's own candles.</para>
/// </summary>
internal sealed class QuoteBars(TimeSpan step)
{
    private Bar? _current;
    private double? _lastVolume;

    /// <summary>Takes one quote and returns the bucket's bar as it now stands, or null for a quote with no
    /// price.</summary>
    public Bar? Push(PolledQuote quote)
    {
        var price = quote.Price;
        if (price <= 0 || double.IsNaN(price)) return null;

        long volume = 0;
        if (quote.DayVolume > 0)
        {
            // A drop means a new session's counter; nothing traded is known for that sample.
            if (_lastVolume is { } previous && quote.DayVolume >= previous) volume = (long)Math.Round(quote.DayVolume - previous);
            _lastVolume = quote.DayVolume;
        }

        var start = BarRollup.BucketStart(quote.TimeUtc, step);
        if (_current is null || start > _current.TimestampUtc)
        {
            _current = new Bar(start, price, price, price, price, volume);
            return _current;
        }

        if (start < _current.TimestampUtc) return _current;

        _current = _current with
        {
            High = Math.Max(_current.High, price),
            Low = Math.Min(_current.Low, price),
            Close = price,
            Volume = _current.Volume + volume,
        };
        return _current;
    }
}

internal static class QuoteBarStreams
{
    /// <summary>Bars built from a stream of polled quotes, yielded as each one changes.</summary>
    public static async IAsyncEnumerable<Bar> BuildAsync(
        IAsyncEnumerable<PolledQuote> quotes, BarSize size, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var bars = new QuoteBars(size.ToTimeSpan());
        Bar? last = null;
        await foreach (var quote in quotes.WithCancellation(ct).ConfigureAwait(false))
        {
            if (bars.Push(quote) is not { } bar || bar == last) continue;
            last = bar;
            yield return bar;
        }
    }
}
