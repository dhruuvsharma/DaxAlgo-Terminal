using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.QuantConnect;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// <see cref="ILeanClient"/> backed by the local open-source <c>lean</c> CLI (runs the engine in
/// Docker). Each operation shells out via <see cref="LeanProcessRunner"/>, streams the engine log,
/// and — for backtests — parses the result JSON LEAN writes under
/// <c>&lt;project&gt;/backtests/&lt;timestamp&gt;/</c>. Everything degrades to an unavailable status or a
/// failed result instead of throwing, so the window stays usable without LEAN installed.
/// </summary>
public sealed class LocalCliLeanClient : ILeanClient
{
    private readonly LeanRuntimeSettings _settings;
    private readonly ILogger<LocalCliLeanClient> _logger;

    public LocalCliLeanClient(LeanRuntimeSettings settings, ILogger<LocalCliLeanClient> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public LeanEngineMode Mode => LeanEngineMode.LocalCli;

    private string Cli => string.IsNullOrWhiteSpace(_settings.CliPath) ? "lean" : _settings.CliPath;
    private string WorkDir => string.IsNullOrWhiteSpace(_settings.ProjectsFolder)
        ? Environment.CurrentDirectory : _settings.ProjectsFolder;

    public async Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
    {
        var outcome = await LeanProcessRunner.RunAsync(Cli, "--version", WorkDir, null, 30, ct).ConfigureAwait(false);
        if (outcome.NotFound)
            return LeanAvailability.Unavailable(
                $"'{Cli}' not found. Install the LEAN CLI (pip install lean) and Docker, or set QuantConnect:CliPath.");
        if (!outcome.Success)
            return LeanAvailability.Unavailable($"'{Cli} --version' exited {outcome.ExitCode}. Is Docker running?");

        var version = outcome.Output.Trim();
        return new LeanAvailability(true, version, $"LEAN CLI detected ({version}).");
    }

    public Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var root = WorkDir;
        var projects = new List<LeanProject>();
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    // A LEAN project folder holds a config.json plus an algorithm entry point.
                    var hasConfig = File.Exists(Path.Combine(dir, "config.json"));
                    var hasPy = File.Exists(Path.Combine(dir, "main.py"));
                    var hasCs = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).Any();
                    if (!hasConfig && !hasPy && !hasCs) continue;

                    var language = hasPy ? "Python" : hasCs ? "C#" : "Unknown";
                    projects.Add(new LeanProject(Path.GetFileName(dir), dir, language));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Listing LEAN projects under {Root} failed", root);
        }

        return Task.FromResult<IReadOnlyList<LeanProject>>(
            projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<LeanBacktestResult> RunBacktestAsync(
        LeanBacktestRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Project))
            return LeanBacktestResult.Failed("Pick a project to backtest.");

        var args = $"backtest \"{request.Project}\"";
        progress?.Report($"$ {Cli} {args}");
        var outcome = await LeanProcessRunner.RunAsync(
            Cli, args, WorkDir, line => progress?.Report(line), _settings.RunTimeoutSeconds, ct)
            .ConfigureAwait(false);

        if (outcome.NotFound)
            return LeanBacktestResult.Failed(
                $"'{Cli}' not found. Install the LEAN CLI and Docker.", outcome.Output);
        if (outcome.ExitCode is null)
            return LeanBacktestResult.Failed("Backtest cancelled or timed out.", outcome.Output);

        var (stats, equity) = TryParseResults(request.Project);
        if (!outcome.Success)
            return new LeanBacktestResult(false, $"lean exited {outcome.ExitCode}", stats, equity, outcome.Output);

        return new LeanBacktestResult(true, null, stats, equity, outcome.Output);
    }

    public async Task<LeanDataResult> DownloadDataAsync(
        LeanDataDownloadRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Build a best-effort `lean data download` invocation from the request. The CLI validates the
        // dataset/argument combination itself and reports any problem in its streamed output.
        var args = $"data download --dataset \"{request.Dataset}\" --ticker \"{request.Tickers}\" --resolution {request.Resolution}";
        if (request.Start is { } s) args += $" --start {s:yyyyMMdd}";
        if (request.End is { } e) args += $" --end {e:yyyyMMdd}";

        progress?.Report($"$ {Cli} {args}");
        var outcome = await LeanProcessRunner.RunAsync(
            Cli, args, WorkDir, line => progress?.Report(line), _settings.RunTimeoutSeconds, ct)
            .ConfigureAwait(false);

        if (outcome.NotFound)
            return LeanDataResult.Failed($"'{Cli}' not found. Install the LEAN CLI and Docker.", outcome.Output);
        if (outcome.ExitCode is null)
            return LeanDataResult.Failed("Download cancelled or timed out.", outcome.Output);
        if (!outcome.Success)
            return new LeanDataResult(false, $"lean exited {outcome.ExitCode}", outcome.Output);

        return new LeanDataResult(true, null, outcome.Output);
    }

    // ── Result parsing ────────────────────────────────────────────────────────────────────────

    /// <summary>Locates the newest <c>&lt;project&gt;/backtests/&lt;ts&gt;/*.json</c> result and pulls out the
    /// statistics map and the equity curve. Best-effort and casing-tolerant — LEAN's JSON shape has
    /// shifted across versions, so any parse failure just yields empty data, never an exception.</summary>
    private (IReadOnlyList<LeanStatistic> Stats, IReadOnlyList<LeanEquityPoint> Equity) TryParseResults(string project)
    {
        try
        {
            var projectPath = Path.IsPathRooted(project) ? project : Path.Combine(WorkDir, project);
            var backtestsDir = Path.Combine(projectPath, "backtests");
            if (!Directory.Exists(backtestsDir)) return (Array.Empty<LeanStatistic>(), Array.Empty<LeanEquityPoint>());

            var latest = Directory.EnumerateDirectories(backtestsDir)
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .FirstOrDefault();
            if (latest is null) return (Array.Empty<LeanStatistic>(), Array.Empty<LeanEquityPoint>());

            // Prefer the main result file (largest .json that isn't an order-events/summary side file).
            var jsonFile = Directory.EnumerateFiles(latest, "*.json")
                .Where(f => !f.EndsWith("order-events.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (jsonFile is null) return (Array.Empty<LeanStatistic>(), Array.Empty<LeanEquityPoint>());

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            var root = doc.RootElement;
            var stats = ParseStatistics(root);
            var equity = ParseEquity(root);
            return (stats, equity);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Parsing LEAN backtest result for {Project} failed", project);
            return (Array.Empty<LeanStatistic>(), Array.Empty<LeanEquityPoint>());
        }
    }

    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static IReadOnlyList<LeanStatistic> ParseStatistics(JsonElement root)
    {
        var list = new List<LeanStatistic>();
        if (TryGetProp(root, "statistics", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in stats.EnumerateObject())
                list.Add(new LeanStatistic(p.Name, p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? "" : p.Value.ToString()));
        }
        return list;
    }

    private static IReadOnlyList<LeanEquityPoint> ParseEquity(JsonElement root)
    {
        var points = new List<LeanEquityPoint>();
        // charts → "Strategy Equity" → series → "Equity" → values[] of {x: unixSeconds, y: value}
        if (!TryGetProp(root, "charts", out var charts) || charts.ValueKind != JsonValueKind.Object)
            return points;
        if (!TryGetProp(charts, "Strategy Equity", out var equityChart) || equityChart.ValueKind != JsonValueKind.Object)
            return points;
        if (!TryGetProp(equityChart, "series", out var series) || series.ValueKind != JsonValueKind.Object)
            return points;
        if (!TryGetProp(series, "Equity", out var equitySeries) || equitySeries.ValueKind != JsonValueKind.Object)
            return points;
        if (!TryGetProp(equitySeries, "values", out var values) || values.ValueKind != JsonValueKind.Array)
            return points;

        foreach (var v in values.EnumerateArray())
        {
            double? x = null, y = null;
            if (v.ValueKind == JsonValueKind.Object)
            {
                if (TryGetProp(v, "x", out var xe) && xe.TryGetDouble(out var xv)) x = xv;
                if (TryGetProp(v, "y", out var ye) && ye.TryGetDouble(out var yv)) y = yv;
            }
            else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 2)
            {
                // Newer LEAN emits [unixSeconds, open, high, low, close]; take time + close.
                var arr = v.EnumerateArray().ToArray();
                if (arr[0].TryGetDouble(out var xv)) x = xv;
                if (arr[^1].TryGetDouble(out var yv)) y = yv;
            }

            if (x is { } xs && y is { } ys)
                points.Add(new LeanEquityPoint(
                    DateTimeOffset.FromUnixTimeSeconds((long)xs).UtcDateTime, ys));
        }
        return points;
    }
}
