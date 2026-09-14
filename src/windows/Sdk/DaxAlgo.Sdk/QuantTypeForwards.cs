using System.Runtime.CompilerServices;
using DaxAlgo.Sdk.Quant;

// The quant library moved to its own assembly, DaxAlgo.Quant, so the Blocks SDK can share it without
// referencing the drawing widgets. Every type it holds used to live here, and units already compiled
// against this assembly — installed packages, in-app authored units — name them as
// [DaxAlgo.Sdk]DaxAlgo.Sdk.Quant.X. These forwards keep each of those names resolving.
//
// QuantTypeForwardsTests fails when a public type is added to DaxAlgo.Quant without a line here.

[assembly: TypeForwardedTo(typeof(Atr))]
[assembly: TypeForwardedTo(typeof(BollingerBands))]
[assembly: TypeForwardedTo(typeof(Book))]
[assembly: TypeForwardedTo(typeof(Camera3))]
[assembly: TypeForwardedTo(typeof(Dema))]
[assembly: TypeForwardedTo(typeof(Ema))]
[assembly: TypeForwardedTo(typeof(EquityStats))]
[assembly: TypeForwardedTo(typeof(EventScore))]
[assembly: TypeForwardedTo(typeof(EwmaVariance))]
[assembly: TypeForwardedTo(typeof(ForecastAccuracy))]
[assembly: TypeForwardedTo(typeof(IEstimator))]
[assembly: TypeForwardedTo(typeof(KalmanHedgeRatio))]
[assembly: TypeForwardedTo(typeof(KalmanLevel))]
[assembly: TypeForwardedTo(typeof(KyleLambda))]
[assembly: TypeForwardedTo(typeof(Macd))]
[assembly: TypeForwardedTo(typeof(Num))]
[assembly: TypeForwardedTo(typeof(OnlineFeatureScaler))]
[assembly: TypeForwardedTo(typeof(OnlineGradientDescent))]
[assembly: TypeForwardedTo(typeof(OnlineLinearRegression))]
[assembly: TypeForwardedTo(typeof(OnlineLogisticRegression))]
[assembly: TypeForwardedTo(typeof(OnlineRegression))]
[assembly: TypeForwardedTo(typeof(OrderFlowImbalance))]
[assembly: TypeForwardedTo(typeof(OrnsteinUhlenbeck))]
[assembly: TypeForwardedTo(typeof(Projected))]
[assembly: TypeForwardedTo(typeof(Projection3))]
[assembly: TypeForwardedTo(typeof(RealizedVolatility))]
[assembly: TypeForwardedTo(typeof(RollingBrierScore))]
[assembly: TypeForwardedTo(typeof(RollingCorrelation))]
[assembly: TypeForwardedTo(typeof(RollingForecastMetrics))]
[assembly: TypeForwardedTo(typeof(RollingWindow))]
[assembly: TypeForwardedTo(typeof(Rsi))]
[assembly: TypeForwardedTo(typeof(Sma))]
[assembly: TypeForwardedTo(typeof(SpreadStats))]
[assembly: TypeForwardedTo(typeof(TradeClassifier))]
[assembly: TypeForwardedTo(typeof(TradeSide))]
[assembly: TypeForwardedTo(typeof(TradeStats))]
[assembly: TypeForwardedTo(typeof(Vec3))]
[assembly: TypeForwardedTo(typeof(Vpin))]
[assembly: TypeForwardedTo(typeof(Vwap))]
[assembly: TypeForwardedTo(typeof(Welford))]
[assembly: TypeForwardedTo(typeof(Wilder))]
[assembly: TypeForwardedTo(typeof(ZScore))]
