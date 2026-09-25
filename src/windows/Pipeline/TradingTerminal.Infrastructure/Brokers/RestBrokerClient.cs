using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// What every broker with a sign-in step does the same way: read the session fresh on every request,
/// prove it at connect, offer the configured instruments, serve history with a rollup for sizes the
/// broker lacks, and make live bars by re-reading the forming candle.
///
/// <para><b>The session is read on every request, never captured.</b> Most of these brokers expire it
/// overnight; a user signs in again in the morning while the terminal is running, and a client that held
/// yesterday's token would fail in a way that looks like a rejected key.</para>
///
/// <para><b>Connect proves the session.</b> A cheap authenticated call — a profile, a quote — is made
/// before reporting Connected, so an expired token is a failed connect that says "sign in again", not a
/// connected client whose every chart is empty.</para>
///
/// <para>Data only. Order routing lives behind its own seam in <c>src/windows/Execution/</c>.</para>
/// </summary>
internal abstract class RestBrokerClient<TOptions> : IBrokerClient where TOptions : SessionBrokerOptions
{
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly IBrokerCredentialSource _credentials;

    protected RestBrokerClient(ILogger logger, TOptions options, IBrokerCredentialSource credentials)
    {
        Logger = logger;
        Options = options;
        _credentials = credentials;
        Http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    protected ILogger Logger { get; }

    protected TOptions Options { get; }

    protected HttpClient Http { get; }

    /// <summary>This broker's credentials and session, as stored now.</summary>
    protected BrokerCredential Credential => _credentials.For(Kind);

    public abstract BrokerKind Kind { get; }

    /// <summary>The broker's name, for logs and the picker.</summary>
    protected abstract string BrokerName { get; }

    /// <summary>What to tell the user when there is no session, or the broker refused it.</summary>
    protected virtual string SignInAdvice =>
        $"Sign in to {BrokerName} in the login window — its sessions expire and need renewing.";

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    /// <summary>Makes one cheap authenticated call; throws when the broker refuses the session.</summary>
    protected abstract Task CheckSessionAsync(CancellationToken ct);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        if (!Credential.HasSession)
        {
            Logger.LogError("{Broker} has no session. {Advice}", BrokerName, SignInAdvice);
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await CheckSessionAsync(cts.Token).ConfigureAwait(false);
            Logger.LogInformation("{Broker} connected.", BrokerName);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Broker} refused the session or could not be reached. {Advice}", BrokerName, SignInAdvice);
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    // ── Instruments ─────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TradableInstrument> list = Options.Instruments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(Parse)
            .DistinctBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(i => new TradableInstrument(
                i.Label is null ? $"{i.Symbol}  —  {BrokerName}" : $"{i.Label} ({i.Symbol})  —  {BrokerName}",
                $"{BrokerName}",
                new Contract(i.Symbol, SecTypeOf(i.Symbol), ExchangeOf(i.Symbol), CurrencyOf(i.Symbol), PrimaryExchange: string.Empty),
                Kind))
            .ToList();
        return Task.FromResult(list);
    }

    /// <summary>An instrument entry is the broker's symbol, optionally followed by <c>|label</c>.</summary>
    internal static (string Symbol, string? Label) Parse(string entry)
    {
        var bar = entry.IndexOf('|');
        return bar < 0 ? (entry.Trim(), null) : (entry[..bar].Trim(), entry[(bar + 1)..].Trim());
    }

    /// <summary>The symbol as sent to the broker: the part before any <c>|label</c>, trimmed.</summary>
    protected static string Symbol(Contract contract) => Parse(contract.Symbol).Symbol;

    protected virtual string SecTypeOf(string symbol) => "STK";

    /// <summary>The exchange, by default the prefix before the first colon (<c>NSE:INFY</c>).</summary>
    protected virtual string ExchangeOf(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon] : BrokerName.ToUpperInvariant();
    }

    protected virtual string CurrencyOf(string symbol) => "INR";

    // ── Bars ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when the broker's history serves <paramref name="size"/> directly.</summary>
    protected abstract bool HasInterval(BarSize size);

    /// <summary>The size to roll <paramref name="size"/> up from when it is not served. One minute by default;
    /// a broker with no 3m but 5m, 30m or 1h can say so and save requests.</summary>
    protected virtual BarSize RollupBase(BarSize size) => BarSize.OneMinute;

    /// <summary>Bars of a natively served size, in any order, covering at least <paramref name="span"/>
    /// back from now where the broker allows.</summary>
    protected abstract Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct);

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        var step = barSize.ToTimeSpan();
        var count = Math.Clamp((int)Math.Ceiling(duration / step), 1, 5000);

        if (HasInterval(barSize))
            return BarRollup.Normalise(await FetchBarsAsync(symbol, barSize, duration, ct).ConfigureAwait(false), count);

        var baseSize = RollupBase(barSize);
        var bars = await FetchBarsAsync(symbol, baseSize, duration, ct).ConfigureAwait(false);
        return BarRollup.Normalise(BarRollup.Rollup(bars, step), count);
    }

    /// <summary>Live bars: the forming candle, re-read from history every <see cref="SessionBrokerOptions.BarPollSeconds"/>.
    /// Exact — it is the broker's own candle — at the cost of a few seconds' lag, which none of these
    /// brokers' candle APIs is faster than anyway.</summary>
    public virtual IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default)
    {
        var step = barSize.ToTimeSpan();
        // Enough history to hold the forming bucket even when it is rolled up from a smaller size.
        var span = step + step;
        return Polling.PollAsync(TimeSpan.FromSeconds(Math.Max(1, Options.BarPollSeconds)), async token =>
        {
            var bars = await RequestHistoricalBarsAsync(contract, barSize, span, token).ConfigureAwait(false);
            return bars.Count > 0 ? bars[^1] : null;
        }, Logger, $"{BrokerName} bars", ct: ct);
    }

    // ── Streams ─────────────────────────────────────────────────────────────────────────────────

    public abstract IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default);

    public abstract IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default);

    /// <summary>Indian and most retail equity feeds publish last price and quantity, not per-print flow with
    /// an aggressor; the terminal's ingest derives trades from L1 instead. Failing loudly beats an empty
    /// stream that looks like a market with no trades.</summary>
    public virtual IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        throw new NotSupportedException($"{BrokerName} publishes no per-trade prints with an aggressor.");

    /// <summary>A polled stream at the configured interval.</summary>
    protected IAsyncEnumerable<T> Poll<T>(string what, Func<CancellationToken, Task<T?>> fetch, CancellationToken ct) where T : class =>
        Polling.PollAsync(TimeSpan.FromMilliseconds(Math.Max(250, Options.PollIntervalMilliseconds)), fetch, Logger, $"{BrokerName} {what}", ct: ct);

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sends, retrying a transport failure twice (see <c>PublicCryptoClient.SendWithRetryAsync</c>),
    /// and throws with the broker's own words on a non-success status.</summary>
    protected async Task<(JsonDocument Doc, string Body)> SendJsonAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"{BrokerName} answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}",
                        null, response.StatusCode);
                return (JsonDocument.Parse(body.Length == 0 ? "{}" : body), body);
            }
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        Http.Dispose();
        _state.Dispose();
    }
}
