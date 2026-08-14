using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtest.Fast;

namespace TradingTerminal.Infrastructure.Backtest.Fast;

/// <summary>
/// Spawns <c>tick_backtester.exe</c> (built from <c>tools/cpp-backtester/</c>) as a
/// one-shot subprocess, pipes a <see cref="FastBacktestRequest"/> in as JSON on stdin,
/// reads a <see cref="FastBacktestResult"/> JSON line back on stdout. Progress / log lines
/// the engine emits on stderr are forwarded to the logger at <c>Debug</c> so they don't
/// pollute the main app's Logs pane unless diagnostics are turned up.
///
/// Strategy identity is a string id, not a delegate — the C++ side has its own strategy
/// set and matches by id. Strategies that don't exist on the C++ side cause a JSON error
/// envelope on stdout, which we translate to <see cref="FastBacktestUnavailableException"/>.
///
/// The exe path is resolved at construction time and cached. If the binary disappears
/// mid-session (deleted, antivirus quarantine), <see cref="RunAsync"/> throws on the next
/// invocation rather than at construction.
/// </summary>
public sealed class ProcessFastBacktestRunner : IFastBacktestRunner
{
    private readonly ILogger<ProcessFastBacktestRunner> _logger;
    private readonly string _exePath;

    public ProcessFastBacktestRunner(ILogger<ProcessFastBacktestRunner> logger, string exePath)
    {
        _logger = logger;
        _exePath = exePath;
    }

    public bool IsAvailable => File.Exists(_exePath);

    public async Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default)
    {
        if (!File.Exists(_exePath))
            throw new FastBacktestUnavailableException(
                $"Fast backtester binary not found at {_exePath}. Build tools/cpp-backtester/ first.");
        if (!File.Exists(request.TickDataParquetPath))
            throw new FileNotFoundException(
                $"Tick data parquet not found: {request.TickDataParquetPath}");

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = "--json",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException(
            $"Failed to launch {_exePath}");

        // stderr is purely informational (progress, info logs). Drain it on a background
        // task so the child can't block on a full pipe.
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                _logger.LogDebug("[tick_backtester] {Line}", line);
        }, ct);

        var requestJson = JsonSerializer.Serialize(request, JsonOpts);
        _logger.LogInformation("Fast backtest start: strategy={Strategy} symbol={Sym} ticks={Path}",
            request.StrategyId, request.Symbol, request.TickDataParquetPath);

        await proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new FastBacktestUnavailableException(
                $"tick_backtester exited with code {proc.ExitCode}. stdout:\n{stdout}");

        // The C++ side emits a single JSON object on stdout. Some runtimes prepend BOM
        // or trailing newline — Deserialize handles both, but if stdout is empty we have
        // nothing to parse and should report the failure clearly.
        if (string.IsNullOrWhiteSpace(stdout))
            throw new FastBacktestUnavailableException(
                "tick_backtester produced no stdout output. Check stderr in the Logs pane.");

        FastBacktestResult? result;
        try
        {
            result = JsonSerializer.Deserialize<FastBacktestResult>(stdout, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new FastBacktestUnavailableException(
                $"Malformed JSON result from tick_backtester. Raw stdout:\n{stdout}", ex);
        }

        if (result is null)
            throw new FastBacktestUnavailableException(
                $"Empty JSON result from tick_backtester. Raw stdout:\n{stdout}");

        _logger.LogInformation("Fast backtest done: trades={Trades} ending={Cash:F2} fees={Fees:F2} engine_ms={Ms:F1}",
            result.Stats.TradeCount, result.EndingCash, result.TotalFees, result.EngineMilliseconds);

        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        IncludeFields = false,
        WriteIndented = false,
    };
}
