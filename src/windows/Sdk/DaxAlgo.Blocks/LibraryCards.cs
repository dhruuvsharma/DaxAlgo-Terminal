using DaxAlgo.Blocks;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.MarketData;

// Blocks that are not interfaces: slices of the maths library, and raw network use. Each is one card,
// so a task that needs indicators is not also sent the online learners. Every public type in the quant
// library belongs to exactly one slice — BlockCardTests fails when a new one is left out.

[assembly: BlockCard(
    "math.averages",
    "smoothers, rolling windows and running statistics",
    Does = "Streaming averages and statistics: feed one value at a time with Update, read Value once IsReady.",
    Needs = "Nothing. Construct with the period, keep the instance, call Update per value.",
    Limits = "O(1) per update. Value is meaningless until IsReady. Reset() to start over; do not re-feed values it has already seen.",
    Types = [typeof(IEstimator), typeof(Ema), typeof(Sma), typeof(Wilder), typeof(Dema), typeof(EwmaVariance), typeof(Welford), typeof(ZScore), typeof(RollingWindow), typeof(Num)],
    Order = 200)]

[assembly: BlockCard(
    "math.indicators",
    "RSI, ATR, MACD, Bollinger bands, VWAP, realised volatility",
    Does = "Standard technical indicators as streaming estimators.",
    Needs = "Prices or bars, one update at a time.",
    Limits = "Wilder-smoothed where the definition says so. Warm-up gated by IsReady.",
    Types = [typeof(Rsi), typeof(Atr), typeof(Macd), typeof(BollingerBands), typeof(Vwap), typeof(RealizedVolatility)],
    Order = 210)]

[assembly: BlockCard(
    "math.regression",
    "regression, correlation, mean reversion and Kalman filters",
    Does = "Online and rolling regression, correlation and beta, an Ornstein-Uhlenbeck half-life fit, and Kalman filters for a level and a hedge ratio.",
    Needs = "Paired or single series, one update at a time.",
    Limits = "Warm-up gated by IsReady.",
    Types = [typeof(OnlineRegression), typeof(RollingCorrelation), typeof(OrnsteinUhlenbeck), typeof(KalmanLevel), typeof(KalmanHedgeRatio)],
    Order = 220)]

[assembly: BlockCard(
    "math.orderflow",
    "trade classification, flow imbalance, VPIN, book and footprint maths",
    Does = "Order-flow and microstructure maths from quotes, prints and depth, including footprint bars built from the tape.",
    Needs = "Quotes, trade prints or depth snapshots from the market block.",
    Limits = "FootprintTimeBucketer is stateful and single-threaded: one per instrument, fed in time order.",
    Types = [typeof(TradeClassifier), typeof(TradeSide), typeof(OrderFlowImbalance), typeof(Vpin), typeof(KyleLambda), typeof(Book), typeof(SpreadStats), typeof(FootprintTimeBucketer), typeof(FootprintFeatures), typeof(FootprintPrint), typeof(FootprintBar), typeof(FootprintFeatureRow)],
    Order = 230)]

[assembly: BlockCard(
    "math.performance",
    "equity-curve and trade statistics",
    Does = "Streaming performance statistics: returns, drawdown, Sharpe-style ratios, win rate and expectancy.",
    Needs = "Equity values or closed trades, one at a time.",
    Limits = "Warm-up gated by IsReady.",
    Types = [typeof(EquityStats), typeof(TradeStats)],
    Order = 240)]

[assembly: BlockCard(
    "math.learning",
    "online learners and forecast scoring",
    Does = "Online feature scaling, linear and logistic regression by gradient descent, and scoring for forecasts and event probabilities.",
    Needs = "Feature vectors and outcomes, one sample at a time.",
    Limits = "SaveState/LoadState round-trip a learner — pair it with the state block to keep learning across restarts.",
    Types = [typeof(OnlineFeatureScaler), typeof(OnlineGradientDescent), typeof(OnlineLinearRegression), typeof(OnlineLogisticRegression), typeof(ForecastAccuracy), typeof(EventScore), typeof(RollingForecastMetrics), typeof(RollingBrierScore)],
    Order = 250)]

[assembly: BlockCard(
    "math.geometry",
    "3D vectors, camera and projection",
    Does = "Vector maths and a camera that projects 3D points to 2D screen coordinates.",
    Needs = "Nothing.",
    Limits = "Pure maths; drawing happens in the unit's page.",
    Types = [typeof(Vec3), typeof(Camera3), typeof(Projection3), typeof(Projected)],
    Order = 260)]

[assembly: BlockCard(
    "network",
    "call external APIs and streams directly (HTTP, WebSocket)",
    Does = "A unit may use System.Net.Http.HttpClient and System.Net.WebSockets.ClientWebSocket to reach any external service — prediction markets, exchanges, data vendors.",
    Needs = "One HttpClient per unit, created in StartAsync and disposed in StopAsync. Hand every result back with schedule.Post before touching unit fields.",
    Limits = "Network callbacks and awaited continuations run on other threads: never touch unit state from them directly. Set a timeout on every request, bound every buffer, and back off on errors. Read HTTP bodies with GetStringAsync or Content.ReadAsStringAsync and parse with JsonDocument or JsonSerializer; read WebSockets with ReceiveAsync into a byte array. System.IO (streams and files), processes and raw threads are not allowed. Start long-running receive loops as tasks from StartAsync and stop them with the CancellationToken in StopAsync; poll with schedule.Every.",
    Order = 300)]
