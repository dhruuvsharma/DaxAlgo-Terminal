using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest.Fast;

namespace TradingTerminal.Infrastructure.Backtest.Fast;

/// <summary>
/// Registers <see cref="IFastBacktestRunner"/> with a real
/// <see cref="ProcessFastBacktestRunner"/> when <c>tick_backtester.exe</c> is found next
/// to the App assembly, falling back to <see cref="NullFastBacktestRunner"/> otherwise.
///
/// The resolved path is the first match of:
///   1. AppContext.BaseDirectory / tick_backtester.exe (where the csproj copy lands)
///   2. AppContext.BaseDirectory / tools/cpp-backtester/bin / tick_backtester.exe
///      (handy during development before the csproj copy fires)
/// </summary>
public static class FastBacktestServiceCollectionExtensions
{
    public static IServiceCollection AddFastBacktestRunner(this IServiceCollection services)
    {
        services.AddSingleton<IFastBacktestRunner>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProcessFastBacktestRunner>>();
            var exePath = ResolveBinary();
            if (exePath is null)
            {
                var nullLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<NullFastBacktestRunner>();
                nullLogger.LogInformation(
                    "tick_backtester.exe not found in {BaseDir} — Fast toggle disabled. " +
                    "Build tools/cpp-backtester/ to enable.", AppContext.BaseDirectory);
                return new NullFastBacktestRunner(
                    $"tick_backtester.exe not found in {AppContext.BaseDirectory}. " +
                    "Build tools/cpp-backtester/ first.");
            }

            logger.LogInformation("Fast backtester resolved: {Path}", exePath);
            return new ProcessFastBacktestRunner(logger, exePath);
        });
        return services;
    }

    private static string? ResolveBinary()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "tick_backtester.exe"),
            Path.Combine(baseDir, "tools", "cpp-backtester", "bin", "tick_backtester.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
