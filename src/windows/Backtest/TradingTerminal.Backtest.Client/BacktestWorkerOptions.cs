using DaxAlgo.Strategy.Bundle;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

public sealed class BacktestWorkerOptions
{
    /// <summary>
    /// Explicit worker exe or dll path. When absent, resolution checks DAXALGO_BACKTEST_WORKER_PATH,
    /// then the staged backtest-worker subdirectory, then the application base directory.
    /// </summary>
    public string? WorkerExecutablePath { get; set; }

    /// <summary>Arguments inserted before the client's mandatory <c>--request path</c> pair.</summary>
    public List<string> WorkerArguments { get; } = [];

    /// <summary>Parent of bounded, one-directory-per-id jobs.</summary>
    public string? JobRootDirectory { get; set; }

    /// <summary>Immutable .daxstrategy store used to prepare fixed worker-job engine images.</summary>
    public string? StrategyBundleStoreRoot { get; set; }

    /// <summary>
    /// Current trust and compatibility policy. Installed-bundle jobs are rejected unless both this
    /// value and <see cref="StrategyBundleStoreRoot"/> are configured.
    /// </summary>
    public StrategyBundleInstallPolicy? StrategyBundlePolicy { get; set; }

    /// <summary>Host ceiling; the effective timeout is the minimum of this, request limit, and deadline.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public int ProgressBufferCapacity { get; set; } = 32;
    public int MaxProgressLineCharacters { get; set; } = 16 * 1024;
    public int MaxCapturedStandardErrorCharacters { get; set; } = 64 * 1024;

    /// <summary>Only worker-owned .staging-* directories older than this are cleaned at launch.</summary>
    public TimeSpan AbandonedStagingAge { get; set; } = TimeSpan.FromDays(2);
}
