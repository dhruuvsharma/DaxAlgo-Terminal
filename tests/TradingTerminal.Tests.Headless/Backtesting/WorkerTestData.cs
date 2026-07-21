using System.Security.Cryptography;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Tests.Backtesting;

internal static class WorkerTestData
{
    public static BacktestJobRequest Request(string jobId = "worker-test")
    {
        var run = new RunSpec(
            Universe.Single(new InstrumentSpec(
                new InstrumentId(1),
                Contract.UsStock("TEST"),
                TickSize: 0.01,
                ContractMultiplier: 1)),
            new DataSpec(),
            StrategyId: "meanReversion",
            Parameters: new StrategyParameters(new Dictionary<string, double>
            {
                ["lookback"] = 20,
                ["entryZ"] = 1.0,
                ["exitZ"] = 0.2,
                ["qty"] = 10,
            }),
            StartingCash: 100_000);
        return BacktestJobRequest.Create(
            jobId,
            run,
            BacktestInputReference.CreateSynthetic(500, "headless_test"),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(BacktestEngine).Assembly.Location))),
            deterministicSeed: 7);
    }

    public static BacktestReportArtifact ReportArtifact() =>
        BacktestReportArtifact.FromReport(new BacktestReport(
            new RunSummary(
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2024, 1, 1, 0, 8, 19, DateTimeKind.Utc),
                100_000,
                100_250,
                500,
                12.5),
            new MetricSet(new Dictionary<string, double> { [MetricSet.Keys.Sharpe] = 1.25 }),
            [],
            [new EquitySample(new DateTime(2024, 1, 1, 0, 8, 19, DateTimeKind.Utc), 100_250, 100_250, 0)],
            [],
            null));
}

internal sealed class WorkerTempDirectory : IDisposable
{
    public WorkerTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DaxAlgoWorkerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { }
    }
}
