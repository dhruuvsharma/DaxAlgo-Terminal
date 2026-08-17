using System.Reflection;
using System.Security.Cryptography;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Worker;

internal static class WorkerApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        var startedUtc = DateTime.UtcNow;
        var invocation = TryParseArguments(args);
        if (invocation is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: TradingTerminal.Backtest.Worker --request <absolute request.json> [--launch-gate <name>]").ConfigureAwait(false);
            return 64;
        }
        if (invocation.LaunchGateName is { } launchGateName)
        {
            try
            {
                using var launchGate = EventWaitHandle.OpenExisting(launchGateName);
                if (!launchGate.WaitOne(TimeSpan.FromSeconds(30)))
                {
                    await Console.Error.WriteLineAsync("The host did not release the worker launch gate within 30 seconds.")
                        .ConfigureAwait(false);
                    return 69;
                }
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                await Console.Error.WriteLineAsync($"Unable to join the worker launch gate: {ex.Message}").ConfigureAwait(false);
                return 69;
            }
        }

        var requestPath = invocation.RequestPath;

        byte[] requestBytes;
        string jobDirectory;
        try
        {
            requestPath = Path.GetFullPath(requestPath);
            if (!File.Exists(requestPath)) throw new FileNotFoundException("The worker request file does not exist.", requestPath);
            jobDirectory = Path.GetDirectoryName(requestPath)
                           ?? throw new InvalidDataException("The request has no containing job directory.");
            await using var requestStream = new FileStream(
                requestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (requestStream.Length is <= 0 or > BacktestProtocolLimits.MaxRequestBytes)
                throw new InvalidDataException(
                    $"The request must be between 1 and {BacktestProtocolLimits.MaxRequestBytes} bytes.");
            requestBytes = GC.AllocateUninitializedArray<byte>(checked((int)requestStream.Length));
            await requestStream.ReadExactlyAsync(requestBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Unable to read worker request: {ex.Message}").ConfigureAwait(false);
            return 65;
        }

        var requestHash = BacktestProtocolHash.ComputeSha256(requestBytes);
        BacktestJobRequest request;
        try
        {
            request = BacktestProtocolJson.Deserialize<BacktestJobRequest>(requestBytes);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            await Console.Error.WriteLineAsync($"Malformed worker request JSON: {ex.Message}").ConfigureAwait(false);
            return 66;
        }

        var emitter = new WorkerProgressEmitter(
            string.IsNullOrWhiteSpace(request.JobId) ? "invalid" : request.JobId,
            Math.Clamp(request.Limits?.MaxProgressMessages ?? 4, 4, BacktestProtocolLimits.MaxProgressMessages));
        var engineHash = BacktestProtocolHash.UnknownSha256;
        var strategyFingerprint = WorkerStrategyFingerprint.Unknown;
        var inputHash = BacktestProtocolHash.UnknownSha256;
        WorkerArtifactPublisher? publisher = null;
        FileStream? inputLease = null;
        BundleStrategyExecution? bundleExecution = null;

        using var consoleCts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            consoleCts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await emitter.EmitAsync(BacktestWorkerPhase.Accepted, "Request accepted.", percentComplete: 0).ConfigureAwait(false);
            await emitter.EmitAsync(BacktestWorkerPhase.Validating, "Validating protocol and immutable inputs.", percentComplete: 2).ConfigureAwait(false);
            BacktestProtocolValidator.Validate(request);
            var limits = request.Limits ?? throw new BacktestProtocolException("missing_limits", "Limits are required.");
            publisher = new WorkerArtifactPublisher(jobDirectory, request, requestHash);

            engineHash = await HashEngineAssemblyAsync().ConfigureAwait(false);
            if (!string.Equals(
                    request.ExpectedHostEngineAssemblySha256,
                    engineHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new BacktestProtocolException(
                    "host_engine_hash_mismatch",
                    "The deployed host backtest engine does not match ExpectedHostEngineAssemblySha256.");
            }
            if (request.Strategy.Source == BacktestStrategySource.Native &&
                request.Strategy.ExpectedAssemblySha256 is { } expected &&
                !string.Equals(expected, engineHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new BacktestProtocolException(
                    "strategy_assembly_hash_mismatch",
                    "The native engine assembly does not match Strategy.ExpectedAssemblySha256.");
            }
            if (request.Strategy.Source == BacktestStrategySource.Native)
            {
                strategyFingerprint = new WorkerStrategyFingerprint(
                    engineHash,
                    null,
                    null,
                    [new BacktestLoadedAssemblyFingerprint(
                        typeof(BacktestEngine).Assembly.GetName().Name ?? "TradingTerminal.Backtest.Engine",
                        engineHash)]);
            }

            var effectiveTimeoutMs = limits.MaxWallClockMilliseconds;
            if (request.DeadlineUtc is { } deadline)
                effectiveTimeoutMs = Math.Min(effectiveTimeoutMs, Math.Max(1, (long)(deadline - DateTime.UtcNow).TotalMilliseconds));

            using var wallClockCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(effectiveTimeoutMs));
            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(consoleCts.Token, wallClockCts.Token);
            var ct = runCts.Token;

            await emitter.EmitAsync(BacktestWorkerPhase.LoadingInput, "Binding immutable market input.", percentComplete: 5, ct: ct)
                .ConfigureAwait(false);
            var feed = await CreateFeedAsync(request, ct).ConfigureAwait(false);
            inputLease = feed.InputLease;
            inputHash = feed.InputSha256;

            IStrategyKernel kernel;
            if (request.Strategy.Source == BacktestStrategySource.Native)
            {
                var registry = new StrategyKernelRegistry(NativeKernels.All);
                if (!registry.TryCreate(request.Strategy.Id, out kernel!))
                    throw new BacktestProtocolException(
                        "unknown_native_kernel",
                        $"Native kernel '{request.Strategy.Id}' is not registered.");
            }
            else
            {
                bundleExecution = await BundleStrategyLoader.LoadAsync(jobDirectory, request)
                    .ConfigureAwait(false);
                kernel = bundleExecution.Kernel;
                strategyFingerprint = new WorkerStrategyFingerprint(
                    bundleExecution.StrategyAssemblySha256,
                    request.Strategy.InstalledBundle!.ContentRootSha256,
                    request.Strategy.InstalledBundle.ArchiveSha256,
                    bundleExecution.Closure);
            }

            var eventsTotal = request.Input.Kind == BacktestInputKind.Synthetic
                ? request.Input.Synthetic!.EventCount
                : (long?)null;
            await emitter.EmitAsync(
                BacktestWorkerPhase.Running,
                "Managed engine started.",
                eventsTotal: eventsTotal,
                percentComplete: null,
                ct: ct).ConfigureAwait(false);

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatTask = emitter.RunHeartbeatAsync(eventsTotal, heartbeatCts.Token);
            BacktestReport report;
            try
            {
                report = await new BacktestEngine(feed.Feed)
                    .RunAsync(request.Run, kernel, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                heartbeatCts.Cancel();
                await heartbeatTask.ConfigureAwait(false);
                if (bundleExecution is not null)
                    await bundleExecution.DisposeAsync().ConfigureAwait(false);
                bundleExecution = null;
            }

            await emitter.EmitAsync(
                BacktestWorkerPhase.Publishing,
                "Publishing hashed result artifacts.",
                eventsProcessed: report.Summary.EventsProcessed,
                eventsTotal: eventsTotal,
                percentComplete: 95,
                ct: ct).ConfigureAwait(false);
            await publisher.PublishSuccessAsync(
                    report,
                    startedUtc,
                    engineHash,
                    strategyFingerprint,
                    inputHash,
                    ct)
                .ConfigureAwait(false);
            await emitter.EmitAsync(
                BacktestWorkerPhase.Completed,
                "Job completed.",
                eventsProcessed: report.Summary.EventsProcessed,
                eventsTotal: eventsTotal,
                percentComplete: 100).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (consoleCts.IsCancellationRequested)
        {
            var error = new BacktestJobError("cancelled", "The worker job was cancelled.");
            await TryPublishFailureAsync(
                publisher,
                BacktestTerminalStatus.Cancelled,
                error,
                startedUtc,
                engineHash,
                strategyFingerprint,
                inputHash).ConfigureAwait(false);
            await TryEmitAsync(emitter, BacktestWorkerPhase.Cancelled, error.Message).ConfigureAwait(false);
            return 4;
        }
        catch (OperationCanceledException)
        {
            var error = new BacktestJobError("deadline_exceeded", "The worker job exceeded its deadline or wall-clock limit.");
            await TryPublishFailureAsync(
                publisher,
                BacktestTerminalStatus.TimedOut,
                error,
                startedUtc,
                engineHash,
                strategyFingerprint,
                inputHash).ConfigureAwait(false);
            await TryEmitAsync(emitter, BacktestWorkerPhase.Failed, error.Message).ConfigureAwait(false);
            return 5;
        }
        catch (BacktestProtocolException ex)
        {
            var error = new BacktestJobError(ex.Code, ex.Message);
            publisher ??= TryCreatePublisher(jobDirectory, request, requestHash);
            await TryPublishFailureAsync(
                publisher,
                BacktestTerminalStatus.ProtocolError,
                error,
                startedUtc,
                engineHash,
                strategyFingerprint,
                inputHash).ConfigureAwait(false);
            await TryEmitAsync(emitter, BacktestWorkerPhase.Failed, ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Protocol error [{ex.Code}]: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex)
        {
            var error = new BacktestJobError(
                "worker_failure",
                ex.Message,
                ex.GetType().FullName,
                Retryable: false);
            publisher ??= TryCreatePublisher(jobDirectory, request, requestHash);
            await TryPublishFailureAsync(
                publisher,
                BacktestTerminalStatus.Failed,
                error,
                startedUtc,
                engineHash,
                strategyFingerprint,
                inputHash).ConfigureAwait(false);
            await TryEmitAsync(emitter, BacktestWorkerPhase.Failed, ex.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Worker failure: {ex}").ConfigureAwait(false);
            return 3;
        }
        finally
        {
            if (bundleExecution is not null)
            {
                try { await bundleExecution.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Strategy cleanup failed after terminal handling: {ex.Message}").ConfigureAwait(false);
                }
            }
            inputLease?.Dispose();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<(IMarketDataFeed Feed, FileStream? InputLease, string InputSha256)> CreateFeedAsync(
        BacktestJobRequest request,
        CancellationToken ct)
    {
        if (request.Input.Kind == BacktestInputKind.Synthetic)
        {
            var synthetic = request.Input.Synthetic!;
            var inputHash = BacktestProtocolHash.ComputeSha256(BacktestProtocolJson.Serialize(request.Input));
            return (
                new SyntheticMarketDataFeed(
                    request.Run.Universe.Primary.Id,
                    synthetic.EventCount,
                    request.DeterministicSeed,
                    synthetic.StartPrice,
                    synthetic.Spread),
                null,
                inputHash);
        }

        if (!request.Run.Universe.IsSingleInstrument)
            throw new BacktestProtocolException(
                "unsupported_parquet_universe",
                "P2 parquet jobs support exactly one instrument.");

        var path = Path.GetFullPath(request.Input.Path!);
        EnsureNoReparseComponents(path);
        var lease = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (lease.Length != request.Input.LengthBytes)
                throw new BacktestProtocolException(
                    "input_length_mismatch",
                    $"Parquet length is {lease.Length}; the request declares {request.Input.LengthBytes}.");
            if (lease.Length > request.Limits.MaxInputBytes)
                throw new BacktestProtocolException(
                    "input_limit_exceeded",
                    $"Parquet length {lease.Length} exceeds the job limit {request.Limits.MaxInputBytes}.");

            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(lease, ct).ConfigureAwait(false));
            lease.Position = 0;
            if (!string.Equals(actualHash, request.Input.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BacktestProtocolException(
                    "input_hash_mismatch",
                    "The parquet bytes do not match the immutable input SHA-256.");

            return (new ParquetMarketDataFeed(lease), lease, actualHash);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new BacktestProtocolException("invalid_input_path", "The parquet path has no filesystem root.");
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BacktestProtocolException(
                    "input_path_unavailable",
                    $"The parquet path component '{current}' could not be inspected.",
                    ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new BacktestProtocolException(
                    "input_reparse_point",
                    "Parquet input paths cannot contain symbolic links, junctions, or other reparse points.");
        }
    }

    private static async Task<string> HashEngineAssemblyAsync()
    {
        var path = typeof(BacktestEngine).Assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The managed engine assembly has no hashable file location.");
        return await BacktestProtocolHash.ComputeFileSha256Async(path).ConfigureAwait(false);
    }

    private static WorkerInvocation? TryParseArguments(string[] args)
    {
        if (args.Length is not (2 or 4)) return null;
        string? requestPath = null;
        string? launchGateName = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) return null;
            switch (args[index])
            {
                case "--request" when requestPath is null:
                    requestPath = args[index + 1];
                    break;
                case "--launch-gate" when launchGateName is null:
                    launchGateName = args[index + 1];
                    break;
                default:
                    return null;
            }
        }

        return requestPath is not null && Path.IsPathFullyQualified(requestPath)
            ? new WorkerInvocation(requestPath, launchGateName)
            : null;
    }

    private sealed record WorkerInvocation(string RequestPath, string? LaunchGateName);

    private static WorkerArtifactPublisher? TryCreatePublisher(
        string jobDirectory,
        BacktestJobRequest request,
        string requestHash)
    {
        try
        {
            BacktestProtocolValidator.ValidateJobId(request.JobId);
            return new WorkerArtifactPublisher(jobDirectory, request, requestHash);
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryPublishFailureAsync(
        WorkerArtifactPublisher? publisher,
        BacktestTerminalStatus status,
        BacktestJobError error,
        DateTime startedUtc,
        string engineHash,
        WorkerStrategyFingerprint strategyFingerprint,
        string inputHash)
    {
        if (publisher is null) return;
        try
        {
            await publisher.PublishFailureAsync(
                status,
                error,
                startedUtc,
                engineHash,
                strategyFingerprint,
                inputHash,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception publishError)
        {
            await Console.Error.WriteLineAsync($"Unable to publish failure manifest: {publishError.Message}")
                .ConfigureAwait(false);
        }
    }

    private static async Task TryEmitAsync(
        WorkerProgressEmitter emitter,
        BacktestWorkerPhase phase,
        string message)
    {
        try { await emitter.EmitAsync(phase, message).ConfigureAwait(false); }
        catch { /* A closed stdout must not hide the terminal failure classification. */ }
    }
}
