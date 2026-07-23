using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.MarketData.Store;

internal sealed record QuestDbRuntimePaths(string ExecutablePath, string RootPath);

/// <summary>Path, configuration, process-start, and health helpers for the bundled QuestDB runtime.</summary>
internal static class QuestDbNativeBootstrapper
{
    internal const string ManagedServerConfiguration =
        "http.net.bind.to=127.0.0.1:9000\n" +
        "http.min.enabled=false\n" +
        "pg.net.bind.to=127.0.0.1:8812\n" +
        "line.tcp.enabled=false\n" +
        "telemetry.enabled=false\n";

    private const string RootOwnershipFileName = ".daxalgo-native-owner.lock";

    public static TimeSpan StartupTimeout(MarketDataStoreOptions options) =>
        TimeSpan.FromSeconds(Math.Max(5, options.QuestDbStartupTimeoutSeconds));

    public static QuestDbRuntimePaths ResolvePaths(
        MarketDataStoreOptions options,
        string? appBaseDirectory = null,
        string? localApplicationData = null)
    {
        var appBase = Path.GetFullPath(appBaseDirectory ?? AppContext.BaseDirectory);
        var localData = Path.GetFullPath(localApplicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var appDataRoot = Path.Combine(localData, "DaxAlgoTerminal");

        var executable = string.IsNullOrWhiteSpace(options.QuestDbExecutablePath)
            ? Path.Combine(appBase, "questdb", "bin", "questdb.exe")
            : ResolveConfiguredPath(options.QuestDbExecutablePath, appBase, "QuestDbExecutablePath");

        var root = string.IsNullOrWhiteSpace(options.QuestDbRootPath)
            ? Path.Combine(appDataRoot, "QuestDB")
            : ResolveConfiguredPath(options.QuestDbRootPath, appDataRoot, "QuestDbRootPath");

        return new QuestDbRuntimePaths(Path.GetFullPath(executable), Path.GetFullPath(root));
    }

    public static void EnsureManagedConfiguration(QuestDbRuntimePaths paths)
    {
        var configurationDirectory = Path.Combine(paths.RootPath, "conf");
        var configurationPath = Path.Combine(configurationDirectory, "server.conf");
        Directory.CreateDirectory(configurationDirectory);

        // Native mode owns this file. Replacing it on every process launch repairs stale or unsafe
        // settings instead of allowing an earlier custom file to silently expose a listener.
        var temporaryPath = configurationPath + ".daxalgo.tmp";
        try
        {
            File.WriteAllText(temporaryPath, ManagedServerConfiguration, new UTF8Encoding(false));
            File.Move(temporaryPath, configurationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Claims exclusive ownership of a native data root across desktop processes. The open handle is
    /// retained for the exact lifetime of the owned child and is released automatically on a crash.
    /// </summary>
    public static bool TryAcquireRootOwnership(
        QuestDbRuntimePaths paths,
        out FileStream? ownership)
    {
        Directory.CreateDirectory(paths.RootPath);
        var lockPath = Path.Combine(paths.RootPath, RootOwnershipFileName);
        try
        {
            ownership = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            ownership = null;
            return false;
        }
    }

    public static ProcessStartInfo CreateStartInfo(QuestDbRuntimePaths paths)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(paths.ExecutablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(paths.RootPath);
        return startInfo;
    }

    public static Process? TryStart(QuestDbRuntimePaths paths, ILogger log)
    {
        if (!File.Exists(paths.ExecutablePath))
        {
            log.LogWarning(
                "QuestDB native runtime was not found at {Path}. Repair the installation or configure " +
                "MarketDataStore:QuestDbExecutablePath.",
                paths.ExecutablePath);
            return null;
        }

        try
        {
            EnsureManagedConfiguration(paths);
            var process = Process.Start(CreateStartInfo(paths));
            if (process is null)
                log.LogWarning("Windows did not create a QuestDB process for {Path}.", paths.ExecutablePath);
            return process;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not start the QuestDB native runtime at {Path}.", paths.ExecutablePath);
            return null;
        }
    }

    public static bool IsReachable(MarketDataStoreOptions options)
    {
        if (!HasSafeEndpoints(options, out _)) return false;

        try
        {
            using var connection = new NpgsqlConnection(options.QuestDbPgConnectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasSafeEndpoints(MarketDataStoreOptions options, out string? reason)
    {
        NpgsqlConnectionStringBuilder pg;
        try
        {
            pg = new NpgsqlConnectionStringBuilder(options.QuestDbPgConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            reason = "QuestDB PG-wire connection string is invalid.";
            return false;
        }

        if (!TryParseIlpEndpoint(options.QuestDbIlpConfig, out var ilpScheme, out var ilpUri))
        {
            reason = "QuestDB ILP-over-HTTP configuration is invalid.";
            return false;
        }

        var pgHost = pg.Host;

        if (options.QuestDbLaunchMode == QuestDbLaunchMode.Native)
        {
            if (string.IsNullOrWhiteSpace(pgHost)
                || !IsIpv4LoopbackOrLocalhost(pgHost)
                || pg.Port != 8812)
            {
                reason = "Native QuestDB PG-wire must use localhost or IPv4 loopback on port 8812.";
                return false;
            }

            if (!string.Equals(ilpScheme, "http", StringComparison.OrdinalIgnoreCase)
                || !IsIpv4LoopbackOrLocalhost(ilpUri.Host)
                || ilpUri.Port != 9000)
            {
                reason = "Native QuestDB ILP must use HTTP on localhost or IPv4 loopback port 9000.";
                return false;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(pgHost) || !IsLoopbackHost(pgHost))
            {
                reason = "External QuestDB PG-wire host must be loopback.";
                return false;
            }

            if ((!string.Equals(ilpScheme, "http", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(ilpScheme, "https", StringComparison.OrdinalIgnoreCase))
                || !IsLoopbackHost(ilpUri.Host))
            {
                reason = "External QuestDB ILP-over-HTTP host must be loopback.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public static string DescribePgEndpoint(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var configuredHost = builder.Host ?? string.Empty;
            var host = configuredHost.Contains(':')
                ? $"[{configuredHost.Trim().TrimStart('[').TrimEnd(']')}]"
                : configuredHost;
            return $"{host}:{builder.Port}";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return "<invalid loopback endpoint>";
        }
    }

    public static async Task<bool> WaitUntilReachableAsync(
        MarketDataStoreOptions options,
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            if (IsReachable(options)) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return IsReachable(options);
    }

    public static async Task<bool> WaitUntilReachableAsync(
        MarketDataStoreOptions options,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReachable(options)) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return IsReachable(options);
    }

    private static string ResolveConfiguredPath(string configured, string relativeRoot, string optionName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(configured.Trim());
        if (Path.IsPathRooted(expanded)) return Path.GetFullPath(expanded);

        var safeRoot = Path.GetFullPath(relativeRoot);
        var candidate = Path.GetFullPath(Path.Combine(safeRoot, expanded));
        if (!IsWithinRoot(candidate, safeRoot))
            throw new InvalidOperationException(
                $"MarketDataStore:{optionName} cannot traverse outside its relative root.");
        return candidate;
    }

    private static bool TryParseIlpEndpoint(string config, out string scheme, out Uri endpoint)
    {
        scheme = string.Empty;
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(config)) return false;

        var schemeSeparator = config.IndexOf("::", StringComparison.Ordinal);
        if (schemeSeparator <= 0) return false;
        scheme = config[..schemeSeparator].Trim();

        var properties = config[(schemeSeparator + 2)..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var address = properties
            .Select(property => property.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts => parts.Length == 2
                && string.Equals(parts[0], "addr", StringComparison.OrdinalIgnoreCase))?[1];

        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate($"{scheme}://{address}", UriKind.Absolute, out var parsed)
            || parsed.Port is <= 0 or > 65535)
            return false;

        endpoint = parsed;
        return true;
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalized = host.Trim().TrimStart('[').TrimEnd(']');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(normalized, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsIpv4LoopbackOrLocalhost(string host)
    {
        var normalized = host.Trim().TrimStart('[').TrimEnd(']');
        if (string.Equals(normalized, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(normalized, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && IPAddress.IsLoopback(address);
    }
}
