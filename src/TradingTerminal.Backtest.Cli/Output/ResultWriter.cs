using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Backtest;

namespace TradingTerminal.Backtest.Cli.Output;

/// <summary>
/// Writes a finished <see cref="BacktestResult"/> to disk: <c>summary.json</c> (stats +
/// metadata), <c>trades.csv</c> (per-trade ledger), <c>equity.csv</c> (equity curve).
/// The output directory is created if missing.
/// </summary>
internal static class ResultWriter
{
    public static async Task WriteAsync(string outputDir, BacktestResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        var summary = new
        {
            result.StartingCash,
            result.EndingCash,
            TotalPnl = result.EndingCash - result.StartingCash,
            Stats = result.Stats,
            TradeCount = result.Trades.Count,
            EquitySamples = result.EquityCurve.Count,
        };

        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        await WriteCsvAsync(
            Path.Combine(outputDir, "trades.csv"),
            "entry_utc,exit_utc,side,quantity,entry_price,exit_price,gross_pnl",
            result.Trades.Select(t => string.Create(CultureInfo.InvariantCulture,
                $"{t.EntryUtc:O},{t.ExitUtc:O},{t.Side},{t.Quantity},{t.EntryPrice},{t.ExitPrice},{t.GrossPnl}")),
            ct).ConfigureAwait(false);

        await WriteCsvAsync(
            Path.Combine(outputDir, "equity.csv"),
            "timestamp_utc,equity",
            result.EquityCurve.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{p.TimestampUtc:O},{p.Equity}")),
            ct).ConfigureAwait(false);
    }

    private static async Task WriteCsvAsync(string path, string header, IEnumerable<string> rows, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var sw = new StreamWriter(fs, new UTF8Encoding(false));
        await sw.WriteLineAsync(header.AsMemory(), ct).ConfigureAwait(false);
        foreach (var row in rows)
            await sw.WriteLineAsync(row.AsMemory(), ct).ConfigureAwait(false);
    }
}
