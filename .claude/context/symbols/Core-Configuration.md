# TradingTerminal.Core / Configuration — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Configuration/AlpacaOptions.cs
```cs
   13: public sealed class AlpacaOptions
   15: public const string SectionName = "Alpaca";
   18: public string ApiKey { get; set; } = string.Empty;
   21: public string ApiSecret { get; set; } = string.Empty;
   24: public bool IsLive { get; set; }
   30: public string StockDataFeed { get; set; } = "iex";
   32: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   33: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/AppEdition.cs
```cs
   12: public enum AppEdition
```

## src/windows/Core/TradingTerminal.Core/Configuration/ArchiveOptions.cs
```cs
    9: public sealed class ArchiveOptions
   11: public const string SectionName = "MarketDataArchive";
   14: public bool Enabled { get; set; } = false;
   17: public ArchivePeriod Period { get; set; } = ArchivePeriod.Weekly;
   20: public ArchiveTables Tables { get; set; } = ArchiveTables.Quotes | ArchiveTables.Bars;
   23: public int DailyCheckHourUtc { get; set; } = 3;
   27: public long MaxPartBytes { get; set; } = 1_900_000_000L;
   31: public bool VerifyAfterUpload { get; set; } = true;
   35: public bool DeleteLocalAfterArchive { get; set; } = true;
   38: public string DefaultTargetKind { get; set; } = "saved";
   42: public string? DefaultTargetChatRef { get; set; }
   46: public string? StagingDirectory { get; set; }
   50: public string? ManifestDatabasePath { get; set; }
```

## src/windows/Core/TradingTerminal.Core/Configuration/BinanceOptions.cs
```cs
   14: public sealed class BinanceOptions
   16: public const string SectionName = "Binance";
   19: public string RestBaseUrl { get; set; } = "https://api.binance.com";
   22: public string WsBaseUrl { get; set; } = "wss://stream.binance.com:9443";
   29: public string[] Instruments { get; set; } =
   41: public double SizeScale { get; set; } = 1000.0;
   43: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   44: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/BrokerEditionPolicy.cs
```cs
   16: public static class BrokerEditionPolicy
   19: public static readonly IReadOnlyList<BrokerKind> Keyless =
   30: public static readonly IReadOnlyList<BrokerKind> Credentialed =
   42: public static IReadOnlyList<BrokerKind> BrokersFor(AppEdition edition) =>
   48: public static bool RequiresCredentials(BrokerKind broker) => Credentialed.Contains(broker);
```

## src/windows/Core/TradingTerminal.Core/Configuration/BybitOptions.cs
```cs
    8: public sealed class BybitOptions
   10: public const string SectionName = "Bybit";
   13: public string RestBaseUrl { get; set; } = "https://api.bybit.com";
   16: public string WsBaseUrl { get; set; } = "wss://stream.bybit.com/v5/public/spot";
   19: public string Category { get; set; } = "spot";
   22: public string[] Instruments { get; set; } =
   29: public double SizeScale { get; set; } = 1000.0;
   32: public int DepthLevels { get; set; } = 50;
   34: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   35: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/CTraderOptions.cs
```cs
   11: public sealed class CTraderOptions
   13: public const string SectionName = "CTrader";
   15: public string Host { get; set; } = "demo.ctraderapi.com";
   16: public int Port { get; set; } = 5035;
   19: public string ClientId { get; set; } = string.Empty;
   22: public string ClientSecret { get; set; } = string.Empty;
   25: public string AccessToken { get; set; } = string.Empty;
   28: public long CtidTraderAccountId { get; set; }
   31: public bool IsLive { get; set; }
   33: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   34: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/CoinbaseOptions.cs
```cs
    9: public sealed class CoinbaseOptions
   11: public const string SectionName = "Coinbase";
   14: public string RestBaseUrl { get; set; } = "https://api.exchange.coinbase.com";
   17: public string WsBaseUrl { get; set; } = "wss://advanced-trade-ws.coinbase.com";
   20: public string[] Instruments { get; set; } =
   27: public double SizeScale { get; set; } = 1000.0;
   30: public int DepthLevels { get; set; } = 20;
   32: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   33: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/DevOptions.cs
```cs
   11: public sealed class DevOptions
   13: public const string SectionName = "Dev";
   20: public bool BypassLogin { get; set; }
   27: public BrokerKind[] AutoConnectBrokers { get; set; } = [];
```

## src/windows/Core/TradingTerminal.Core/Configuration/InteractiveBrokersOptions.cs
```cs
    3: public sealed class InteractiveBrokersOptions
    5: public const string SectionName = "InteractiveBrokers";
    7: public string Host { get; set; } = "127.0.0.1";
    8: public int Port { get; set; } = 7497;
    9: public int ClientId { get; set; } = 1;
   10: public string AccountType { get; set; } = "Paper";
   12: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   13: public int ReconnectMaxDelaySeconds { get; set; } = 30;
   23: public int MarketDataType { get; set; } = 1;
```

## src/windows/Core/TradingTerminal.Core/Configuration/IronBeamOptions.cs
```cs
   15: public sealed class IronBeamOptions
   17: public const string SectionName = "IronBeam";
   20: public string Username { get; set; } = string.Empty;
   26: public string ApiKey { get; set; } = string.Empty;
   32: public bool IsLive { get; set; }
   38: public string BaseUrlOverride { get; set; } = string.Empty;
   40: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   41: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/KrakenOptions.cs
```cs
    9: public sealed class KrakenOptions
   11: public const string SectionName = "Kraken";
   14: public string RestBaseUrl { get; set; } = "https://api.kraken.com";
   17: public string WsBaseUrl { get; set; } = "wss://ws.kraken.com/v2";
   20: public string[] Instruments { get; set; } =
   27: public double SizeScale { get; set; } = 1000.0;
   30: public int DepthLevels { get; set; } = 10;
   32: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   33: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/LondonStrategicEdgeOptions.cs
```cs
   17: public sealed class LondonStrategicEdgeOptions
   19: public const string SectionName = "LondonStrategicEdge";
   22: public string ApiKey { get; set; } = string.Empty;
   25: public string WsUrl { get; set; } = "wss://data-ws.londonstrategicedge.com";
   28: public string RestBaseUrl { get; set; } = "https://api.londonstrategicedge.com/iso";
   31: public string CatalogUrl { get; set; } = "https://londonstrategicedge.com/feed-catalog.json";
   34: public int PingIntervalSeconds { get; set; } = 25;
   36: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   37: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/MarketDataStoreOptions.cs
```cs
    4: public enum MarketDataProvider
   36: public sealed class MarketDataStoreOptions
   38: public const string SectionName = "MarketDataStore";
   41: public bool Enabled { get; set; } = true;
   45: public MarketDataProvider Provider { get; set; } = MarketDataProvider.SqlitePerBroker;
   49: public string PostgresConnectionString { get; set; } =
   53: public string DatabasePath { get; set; } = string.Empty;
   57: public bool PersistLiveData { get; set; } = true;
   60: public int WriteBatchSize { get; set; } = 500;
   63: public int FlushIntervalMs { get; set; } = 1000;
   70: public int QuoteRetentionDays { get; set; } = 30;
   73: public int TradeRetentionDays { get; set; } = 30;
   77: public int BarRetentionDays { get; set; } = 0;
   87: public string QuestDbIlpConfig { get; set; } = "http::addr=localhost:9000;auto_flush=off;";
   91: public string QuestDbPgConnectionString { get; set; } =
   97: public int DepthRetentionDays { get; set; } = 14;
  107: public bool AutoStartDocker { get; set; } = true;
  110: public string DockerComposeService { get; set; } = "questdb";
  114: public string DockerContainerName { get; set; } = "daxalgo-questdb";
  117: public int DockerStartupTimeoutSeconds { get; set; } = 40;
  121: public string DockerDesktopPath { get; set; } = string.Empty;
```

## src/windows/Core/TradingTerminal.Core/Configuration/MarketRegimeOptions.cs
```cs
   14: public sealed class MarketRegimeOptions
   16: public const string SectionName = "MarketRegime";
   19: public bool Enabled { get; set; } = true;
   24: public int RefreshMinutes { get; set; } = 30;
   29: public string FredApiKey { get; set; } = string.Empty;
   33: public bool UseCnnFearGreed { get; set; } = true;
   37: public bool UseAaiiSentiment { get; set; } = true;
   41: public bool NotifyOnRegimeChange { get; set; } = true;
   46: public bool GateSignalsWhenRiskOff { get; set; }
   50: public double RiskOffThreshold { get; set; } = 40;
```

## src/windows/Core/TradingTerminal.Core/Configuration/ModelRegistryOptions.cs
```cs
    8: public sealed class ModelRegistryOptions
   10: public const string SectionName = "ModelRegistry";
   14: public string DatabasePath { get; set; } = string.Empty;
   18: public int RetentionDays { get; set; } = 0;
```

## src/windows/Core/TradingTerminal.Core/Configuration/NinjaTraderOptions.cs
```cs
    8: public sealed class NinjaTraderOptions
   10: public const string SectionName = "NinjaTrader";
   13: public string AccountName { get; set; } = "Sim101";
   21: public string DllPath { get; set; } = string.Empty;
   27: public string DefaultFuturesContractMonth { get; set; } = string.Empty;
   29: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   30: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/OkxOptions.cs
```cs
    9: public sealed class OkxOptions
   11: public const string SectionName = "Okx";
   14: public string RestBaseUrl { get; set; } = "https://www.okx.com";
   17: public string WsBaseUrl { get; set; } = "wss://ws.okx.com:8443/ws/v5/public";
   20: public string[] Instruments { get; set; } =
   27: public double SizeScale { get; set; } = 1000.0;
   29: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   30: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```

## src/windows/Core/TradingTerminal.Core/Configuration/OrderFlowPressureMapOptions.cs
```cs
    9: public enum PressureSignal
   28: public enum PressureUniverse
   38: public enum SignalTypeFilter
   60: public sealed record OrderFlowPressureMapOptions
   63: public const string SectionName = "OrderFlowPressureMap";
   67: public PressureUniverse Universe { get; init; } = PressureUniverse.Sp100;
   71: public double MinRelativeVolume { get; init; } = 2.0;
   74: public SignalTypeFilter SignalFilter { get; init; } = SignalTypeFilter.All;
   77: public bool ShowOnlyActive { get; init; } = false;
   80: public int GuiRefreshMs { get; init; } = 1000;
   83: public int DisplayWindowColumns { get; init; } = 60;
   87: public int BaselineDays { get; init; } = 20;
   91: public int ShortBaselineMinutes { get; init; } = 30;
   94: public int Atr14Period { get; init; } = 14;
   98: public int MaxConcurrentSubscriptions { get; init; } = 100;
  102: public bool EnableDepth { get; init; } = false;
  108: public double AbsorptionMaxPriceImpact { get; init; } = 0.35;
  112: public double BreakthroughMinPriceImpact { get; init; } = 0.60;
  115: public double BullishAbsorptionMinCandlePosition { get; init; } = 0.55;
  118: public double BearishAbsorptionMaxCandlePosition { get; init; } = 0.45;
  121: public double BullishBreakthroughMinCandlePosition { get; init; } = 0.75;
  124: public double BearishBreakdownMaxCandlePosition { get; init; } = 0.25;
  128: public double BookImbalanceThreshold { get; init; } = 0.10;
```

## src/windows/Core/TradingTerminal.Core/Configuration/ParquetLakeOptions.cs
```cs
   13: public sealed class ParquetLakeOptions
   15: public const string SectionName = "MarketDataParquetLake";
   18: public bool Enabled { get; set; } = false;
   22: public string? RootDirectory { get; set; }
   26: public ArchivePeriod Period { get; set; } = ArchivePeriod.Monthly;
   29: public ArchiveTables Tables { get; set; } = ArchiveTables.Quotes | ArchiveTables.Bars | ArchiveTables.Trades;
   33: public int DailyCheckHourUtc { get; set; } = 4;
```

## src/windows/Core/TradingTerminal.Core/Configuration/PluginsOptions.cs
```cs
    4: public enum PluginTrustMode
   18: public enum PluginScanMode
   37: public sealed class PluginsOptions
   39: public const string SectionName = "Plugins";
   44: public PluginTrustMode TrustPolicy { get; set; } = PluginTrustMode.Permissive;
   48: public IList<string> TrustedThumbprints { get; set; } = [];
   53: public PluginScanMode ScanMode { get; set; } = PluginScanMode.Enforce;
```

## src/windows/Core/TradingTerminal.Core/Configuration/ResearchReproOptions.cs
```cs
   10: public sealed class ResearchReproOptions
   12: public const string SectionName = "ResearchRepro";
   15: public bool Enabled { get; set; } = false;
   22: public string SidecarBaseUrl { get; set; } = "";
   25: public int SidecarTimeoutSeconds { get; set; } = 60;
   28: public SandboxKind SandboxKind { get; set; } = SandboxKind.Docker;
   31: public int RetentionDays { get; set; } = 90;
   35: public string? JobDatabasePath { get; set; }
```

## src/windows/Core/TradingTerminal.Core/Configuration/SandboxOptions.cs
```cs
   10: public sealed class SandboxOptions
   12: public const string SectionName = "Sandbox";
   15: public SandboxKind Kind { get; set; } = SandboxKind.Docker;
   20: public bool AutoStartDocker { get; set; } = true;
   23: public int MaxConcurrent { get; set; } = 1;
   27: public string BaseImage { get; set; } = "python:3.11-slim";
   30: public double Cpus { get; set; } = SandboxQuota.Strict.Cpus;
   31: public int MemoryMb { get; set; } = SandboxQuota.Strict.MemoryMb;
   32: public int PidsLimit { get; set; } = SandboxQuota.Strict.PidsLimit;
   33: public int DiskMb { get; set; } = SandboxQuota.Strict.DiskMb;
   34: public int WallClockSeconds { get; set; } = (int)SandboxQuota.Strict.WallClock.TotalSeconds;
   37: public SandboxQuota ToQuota() =>
```

## src/windows/Core/TradingTerminal.Core/Configuration/SidecarOptions.cs
```cs
    9: public sealed class SidecarOptions
   11: public const string SectionName = "Sidecar";
   15: public bool AutoStart { get; set; } = true;
   19: public int Port { get; set; } = 8765;
   23: public string ExecutablePath { get; set; } = "";
   27: public string PythonPath { get; set; } = "";
   30: public int StartupTimeoutSeconds { get; set; } = 40;
```

## src/windows/Core/TradingTerminal.Core/Configuration/SimulatedBrokerOptions.cs
```cs
    6: public enum SimulatedFeedMode
   22: public sealed class SimulatedBrokerOptions
   24: public const string SectionName = "SimulatedBroker";
   27: public SimulatedFeedMode Mode { get; set; } = SimulatedFeedMode.Synthetic;
   33: public double SpeedMultiplier { get; set; } = 1.0;
   37: public bool Loop { get; set; } = true;
   41: public int MaxGapSeconds { get; set; } = 2;
   44: public int ReplayLookbackDays { get; set; } = 30;
   49: public BarSize SyntheticBarSize { get; set; } = BarSize.OneMinute;
   52: public int SyntheticTickIntervalMs { get; set; } = 250;
   55: public int SyntheticBarIntervalMs { get; set; } = 2000;
   58: public double SyntheticStartPrice { get; set; } = 100.0;
   61: public double SyntheticVolatility { get; set; } = 0.0015;
   64: public int Seed { get; set; } = 1234;
   68: public string[] Instruments { get; set; } = ["AAPL", "MSFT", "ES", "NQ", "BTCUSD"];
```

## src/windows/Core/TradingTerminal.Core/Configuration/TelegramArchiveOptions.cs
```cs
   11: public sealed class TelegramArchiveOptions
   13: public const string SectionName = "TelegramArchive";
   16: public int ApiId { get; set; }
   20: public string ApiHash { get; set; } = string.Empty;
   23: public string PhoneNumber { get; set; } = string.Empty;
   27: public string? SessionFilePath { get; set; }
   32: public string? ApiHashEncryptedBase64 { get; set; }
   36: public string? PhoneNumberEncryptedBase64 { get; set; }
```

## src/windows/Core/TradingTerminal.Core/Configuration/UpstoxOptions.cs
```cs
   20: public sealed class UpstoxOptions
   22: public const string SectionName = "Upstox";
   25: public string ApiKey { get; set; } = string.Empty;
   28: public string ApiSecret { get; set; } = string.Empty;
   31: public string RedirectUri { get; set; } = string.Empty;
   37: public string AccessToken { get; set; } = string.Empty;
   42: public string BaseUrl { get; set; } = "https://api.upstox.com";
   44: public int ReconnectInitialDelaySeconds { get; set; } = 1;
   45: public int ReconnectMaxDelaySeconds { get; set; } = 30;
```
