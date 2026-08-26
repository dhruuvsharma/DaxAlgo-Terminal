namespace TradingTerminal.Core.Brokers;

/// <summary>Where a broker's customers mostly are. Used to group a long picker, not to gate anything.</summary>
public enum BrokerRegion
{
    Global,
    UnitedStates,
    Europe,
    India,
    AsiaPacific,
    Crypto,
}

/// <summary>What a broker lets you trade. Flags, because most of them span more than one.</summary>
[Flags]
public enum BrokerAssets
{
    None = 0,
    Equities = 1 << 0,
    Options = 1 << 1,
    Futures = 1 << 2,
    Forex = 1 << 3,
    Cfd = 1 << 4,
    Crypto = 1 << 5,
    Bonds = 1 << 6,
}

/// <summary>How a connection is authorised. It decides what the login form has to ask for, which is the
/// only part of onboarding that differs much between brokers.</summary>
public enum BrokerAuth
{
    /// <summary>Nothing to enter — a public market-data feed.</summary>
    None,

    /// <summary>An API key, usually with a secret.</summary>
    ApiKey,

    /// <summary>A browser round trip.</summary>
    OAuth,

    /// <summary>Username and password against the broker's own gateway.</summary>
    Credentials,

    /// <summary>A program the user installs and the terminal talks to locally — TWS, NinjaTrader,
    /// MetaTrader, a Futu gateway.</summary>
    LocalGateway,
}

/// <summary>
/// How far a broker has actually got. <b>Stated rather than implied</b>, because the alternative is a
/// picker that lists a broker, accepts a key, and then does nothing — which reads as a broken terminal
/// rather than an unfinished integration.
/// </summary>
public enum BrokerStatus
{
    /// <summary>Known, catalogued, no adapter yet.</summary>
    Planned,

    /// <summary>An adapter exists but has never been run against a funded live account.</summary>
    Unverified,

    /// <summary>Market data works and is verified. No order routing.</summary>
    DataOnly,

    /// <summary>Data and order execution both work.</summary>
    Full,
}

/// <summary>
/// One broker the terminal knows about.
/// </summary>
/// <param name="Id">Stable slug. Also the logo file name, so the two cannot drift apart.</param>
/// <param name="DisplayName">What the user is shown.</param>
/// <param name="Domain">The owner's official site — provenance for the mark, and where "learn more" goes.</param>
/// <param name="Region">Where its customers mostly are.</param>
/// <param name="Assets">What it lets you trade.</param>
/// <param name="Auth">What its login form has to ask for.</param>
/// <param name="Status">How far the integration has actually got.</param>
/// <param name="Kind">The wire-level source, once one exists. Null while the integration is planned.</param>
/// <param name="Note">One line of anything a user or an implementer would want to know first.</param>
public sealed record BrokerProfile(
    string Id,
    string DisplayName,
    string Domain,
    BrokerRegion Region,
    BrokerAssets Assets,
    BrokerAuth Auth,
    BrokerStatus Status,
    BrokerKind? Kind = null,
    string? Note = null)
{
    /// <summary>Repository-relative path to the mark, or null when there is none and the caller should
    /// fall back to text.</summary>
    public string LogoAsset => $"assets/brokers/{Id}.png";

    /// <summary>True when a user can connect this today.</summary>
    public bool IsConnectable => Status is not BrokerStatus.Planned;

    /// <summary>True when a strategy could route an order through it.</summary>
    public bool CanExecute => Status is BrokerStatus.Full;
}

/// <summary>
/// Every broker the terminal knows about, whether or not it can talk to it yet.
///
/// <para>One list, because the alternative is what was here before: a broker's identity spread across a
/// <c>BrokerKind</c> value, a login form, a DI registration, a logo file and a handful of switch
/// statements, with nothing joining them and nothing able to answer "what do we support?" without a
/// developer reading code.</para>
///
/// <para><b>A catalogue entry is not an adapter.</b> <see cref="BrokerProfile.Kind"/> is null until one
/// exists, and <see cref="BrokerProfile.Status"/> says so out loud. That distinction is the point:
/// listing a broker is cheap and useful — it tells a user their broker is on the map — while pretending
/// it connects would be a picker that takes an API key and does nothing with it.</para>
///
/// <para><see cref="BrokerKind"/> deliberately does not gain a value per catalogue entry. It travels on
/// every quote and bar as provenance and lives in a published contract package, so it means "a source
/// that can produce data" and gains a member only when something can.</para>
/// </summary>
public static class BrokerCatalog
{
    /// <summary>Every known broker, in no particular order — callers group by region or status.</summary>
    public static IReadOnlyList<BrokerProfile> All { get; } =
    [
        // ── Connected today ─────────────────────────────────────────────────────────────────────
        new("interactive-brokers", "Interactive Brokers", "interactivebrokers.com",
            BrokerRegion.Global, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures
            | BrokerAssets.Forex | BrokerAssets.Bonds, BrokerAuth.LocalGateway, BrokerStatus.Full,
            BrokerKind.InteractiveBrokers, "Needs TWS or IB Gateway running locally."),

        new("ctrader", "cTrader", "ctrader.com",
            BrokerRegion.Global, BrokerAssets.Forex | BrokerAssets.Cfd, BrokerAuth.OAuth,
            BrokerStatus.Full, BrokerKind.CTrader,
            "One integration reaches every cTrader broker — Pepperstone, IC Markets, FxPro and the rest."),

        new("alpaca", "Alpaca", "alpaca.markets",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Crypto, BrokerAuth.ApiKey,
            BrokerStatus.Full, BrokerKind.Alpaca, "Paper and live; live stock stream pinned to IEX."),

        new("ninjatrader", "NinjaTrader", "ninjatrader.com",
            BrokerRegion.UnitedStates, BrokerAssets.Futures, BrokerAuth.LocalGateway,
            BrokerStatus.DataOnly, BrokerKind.NinjaTrader, "Talks to a local NinjaTrader 8 install."),

        new("ironbeam", "Ironbeam", "ironbeam.com",
            BrokerRegion.UnitedStates, BrokerAssets.Futures, BrokerAuth.Credentials,
            BrokerStatus.DataOnly, BrokerKind.IronBeam, "Futures FCM."),

        new("london-strategic-edge", "London Strategic Edge", "londonstrategicedge.com",
            BrokerRegion.Europe, BrokerAssets.Equities | BrokerAssets.Forex | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.DataOnly, BrokerKind.LondonStrategicEdge,
            "Free multi-asset level 1 and history."),

        new("upstox", "Upstox", "upstox.com",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.OAuth, BrokerStatus.DataOnly, BrokerKind.Upstox),

        new("binance", "Binance", "binance.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.DataOnly,
            BrokerKind.Binance, "Public feed; no key needed for market data. Offered both keyless and keyed — either way, the same venue and the same client."),

        new("coinbase", "Coinbase", "coinbase.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.Unverified,
            BrokerKind.Coinbase,
            "Public market data needs no account; a key raises the rate-limit budget. Both ways in are offered — either way, it is the same venue and the same client."),

        new("bybit", "Bybit", "bybit.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.Unverified,
            BrokerKind.Bybit,
            "Public market data needs no account; a key raises the rate-limit budget. Both ways in are offered — either way, it is the same venue and the same client."),

        new("kraken", "Kraken", "kraken.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.Unverified,
            BrokerKind.Kraken,
            "Public market data needs no account; a key raises the rate-limit budget. Both ways in are offered — either way, it is the same venue and the same client."),

        new("okx", "OKX", "okx.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.Unverified,
            BrokerKind.Okx,
            "Public market data needs no account; a key raises the rate-limit budget. Both ways in are offered — either way, it is the same venue and the same client."),

        // ── United States ───────────────────────────────────────────────────────────────────────
        new("charles-schwab", "Charles Schwab", "schwab.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.OAuth, BrokerStatus.Planned, Note:
            "Absorbed TD Ameritrade and thinkorswim. Largest US retail audience; keys need approval."),

        new("tradestation", "TradeStation", "tradestation.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.OAuth, BrokerStatus.Planned, Note: "REST plus streaming, well documented."),

        new("tradier", "Tradier", "tradier.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options, BrokerAuth.ApiKey,
            BrokerStatus.Planned, Note: "Developer-first, options-heavy, inexpensive market data."),

        new("tastytrade", "tastytrade", "tastytrade.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.Credentials, BrokerStatus.Planned, Note: "Strong options following."),

        new("etrade", "E*TRADE", "etrade.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options, BrokerAuth.OAuth,
            BrokerStatus.Planned, Note: "OAuth 1.0a — older style than the rest of this list."),

        new("das-trader", "DAS Trader", "dastrader.com",
            BrokerRegion.UnitedStates, BrokerAssets.Equities | BrokerAssets.Options,
            BrokerAuth.LocalGateway, BrokerStatus.Planned, Note: "Direct-access; active-trader niche."),

        // ── Futures infrastructure ──────────────────────────────────────────────────────────────
        new("rithmic", "Rithmic", "rithmic.com",
            BrokerRegion.UnitedStates, BrokerAssets.Futures, BrokerAuth.Credentials, BrokerStatus.Planned,
            Note: "Not a broker — the backbone behind AMP, Optimus and most prop firms. "
                + "One adapter reaches dozens of them."),

        new("cqg", "CQG", "cqg.com",
            BrokerRegion.UnitedStates, BrokerAssets.Futures, BrokerAuth.Credentials, BrokerStatus.Planned,
            Note: "The other futures backbone. Same leverage as Rithmic."),

        new("tradovate", "Tradovate", "tradovate.com",
            BrokerRegion.UnitedStates, BrokerAssets.Futures, BrokerAuth.Credentials, BrokerStatus.Planned,
            Note: "Modern REST plus WebSocket; popular with retail futures and prop accounts."),

        // ── Forex and CFD ───────────────────────────────────────────────────────────────────────
        new("oanda", "OANDA", "oanda.com",
            BrokerRegion.Global, BrokerAssets.Forex | BrokerAssets.Cfd, BrokerAuth.ApiKey,
            BrokerStatus.Unverified, BrokerKind.Oanda,
            "v20 REST and streaming. Written against the published reference; not yet run against a "
            + "funded account, so it is Unverified rather than DataOnly."),

        new("saxo-bank", "Saxo Bank", "home.saxo",
            BrokerRegion.Europe, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures
            | BrokerAssets.Forex | BrokerAssets.Bonds, BrokerAuth.OAuth, BrokerStatus.Planned,
            Note: "OpenAPI; genuinely multi-asset."),

        new("ig-group", "IG", "ig.com",
            BrokerRegion.Europe, BrokerAssets.Cfd | BrokerAssets.Forex | BrokerAssets.Equities,
            BrokerAuth.ApiKey, BrokerStatus.Planned, Note: "REST plus Lightstreamer streaming."),

        new("forex-com", "FOREX.com", "forex.com",
            BrokerRegion.UnitedStates, BrokerAssets.Forex | BrokerAssets.Cfd, BrokerAuth.Credentials,
            BrokerStatus.Planned, Note: "StoneX. US-regulated FX."),

        new("dukascopy", "Dukascopy", "dukascopy.com",
            BrokerRegion.Europe, BrokerAssets.Forex | BrokerAssets.Cfd, BrokerAuth.Credentials,
            BrokerStatus.Planned, Note: "Swiss bank; JForex or FIX. Known for tick-data quality."),

        new("swissquote", "Swissquote", "swissquote.com",
            BrokerRegion.Europe, BrokerAssets.Forex | BrokerAssets.Equities, BrokerAuth.ApiKey,
            BrokerStatus.Planned),

        new("metatrader", "MetaTrader 4 / 5", "metatrader5.com",
            BrokerRegion.Global, BrokerAssets.Forex | BrokerAssets.Cfd, BrokerAuth.LocalGateway,
            BrokerStatus.Planned, Note:
            "Not a broker — a platform hundreds of brokers use. Reaching it means a local bridge, "
            + "since there is no official cross-platform API."),

        // ── India ───────────────────────────────────────────────────────────────────────────────
        new("zerodha", "Zerodha", "zerodha.com",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.OAuth, BrokerStatus.Planned,
            Note: "Kite Connect. India's largest retail broker by a wide margin; the API is paid."),

        new("angel-one", "Angel One", "angelone.in",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.Planned, Note: "SmartAPI, free."),

        new("dhan", "Dhan", "dhan.co",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.Planned, Note: "Developer-friendly and free."),

        new("fyers", "Fyers", "fyers.in",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.OAuth, BrokerStatus.Planned),

        new("5paisa", "5paisa", "5paisa.com",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("alice-blue", "Alice Blue", "aliceblueonline.com",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("groww", "Groww", "groww.in",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options, BrokerAuth.ApiKey,
            BrokerStatus.Planned, Note: "Newer API — confirm current availability before building."),

        new("icici-direct", "ICICI Direct", "icicidirect.com",
            BrokerRegion.India, BrokerAssets.Equities | BrokerAssets.Options | BrokerAssets.Futures,
            BrokerAuth.ApiKey, BrokerStatus.Planned,
            Note: "Breeze API. No mark available, so the picker shows the text fallback."),

        // ── Crypto ──────────────────────────────────────────────────────────────────────────────
        new("hyperliquid", "Hyperliquid", "hyperliquid.xyz",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.None, BrokerStatus.Unverified,
            BrokerKind.Hyperliquid,
            "Perpetuals DEX. Wire shapes verified against the live venue; keys exist for trading, not "
            + "for reading, so market data needs no account at all."),

        new("deribit", "Deribit", "deribit.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto | BrokerAssets.Options, BrokerAuth.None,
            BrokerStatus.Unverified, BrokerKind.Deribit,
            "Where crypto options actually trade. Every wire shape was verified against the live venue, "
            + "but no funded account has run through it, so it is Unverified rather than DataOnly."),

        new("bitget", "Bitget", "bitget.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("kucoin", "KuCoin", "kucoin.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("gate-io", "Gate.io", "gate.io",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("gemini", "Gemini", "gemini.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned,
            Note: "US-regulated."),

        new("crypto-com", "Crypto.com", "crypto.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned),

        new("upbit", "Upbit", "upbit.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned,
            Note: "Korea's largest exchange."),

        new("bithumb", "Bithumb", "bithumb.com",
            BrokerRegion.Crypto, BrokerAssets.Crypto, BrokerAuth.ApiKey, BrokerStatus.Planned),

        // ── Asia-Pacific ────────────────────────────────────────────────────────────────────────
        new("futu", "Futu / moomoo", "futunn.com",
            BrokerRegion.AsiaPacific, BrokerAssets.Equities | BrokerAssets.Options,
            BrokerAuth.LocalGateway, BrokerStatus.Planned,
            Note: "OpenAPI through a gateway the user runs locally."),

        new("tiger-brokers", "Tiger Brokers", "itiger.com",
            BrokerRegion.AsiaPacific, BrokerAssets.Equities | BrokerAssets.Options,
            BrokerAuth.ApiKey, BrokerStatus.Planned),
    ];

    /// <summary>Looks one up by slug, or null.</summary>
    public static BrokerProfile? Find(string id) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile behind a wire-level source, or null for one that has no adapter.</summary>
    public static BrokerProfile? For(BrokerKind kind) => All.FirstOrDefault(b => b.Kind == kind);

    /// <summary>The ones a user can connect today.</summary>
    public static IReadOnlyList<BrokerProfile> Connectable =>
        [.. All.Where(b => b.IsConnectable).OrderBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The ones on the map but not yet reachable — shown so a user can see their broker is
    /// coming rather than concluding it is unsupported.</summary>
    public static IReadOnlyList<BrokerProfile> Planned =>
        [.. All.Where(b => b.Status == BrokerStatus.Planned)
               .OrderBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Grouped for a picker.</summary>
    public static IReadOnlyList<IGrouping<BrokerRegion, BrokerProfile>> ByRegion =>
        [.. All.GroupBy(b => b.Region).OrderBy(g => g.Key)];
}
