using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using DaxAlgo.Strategy.Bundle;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Infrastructure.Sidecar;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

/// <summary>
/// Owns one-shot worker processes. Every control stream and captured diagnostic is bounded; cancel,
/// timeout, protocol failure, disposal, and memory-limit breaches kill the complete process tree.
/// </summary>
public sealed class BacktestJobClient : IBacktestJobClient, IDisposable
{
    private readonly BacktestWorkerOptions _options;
    private readonly ILogger<BacktestJobClient> _logger;
    private readonly ConcurrentDictionary<int, ActiveWorker> _active = new();
    private readonly ConcurrentDictionary<int, byte> _disposedProcesses = new();
    private int _disposed;

    public BacktestJobClient(
        IOptions<BacktestWorkerOptions> options,
        ILogger<BacktestJobClient> logger)
        : this(options.Value, logger)
    {
    }

    public BacktestJobClient(
        BacktestWorkerOptions options,
        ILogger<BacktestJobClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<BacktestJobClient>.Instance;
    }

    public async Task<BacktestJobOutcome> RunAsync(
        BacktestJobRequest request,
        IProgress<BacktestJobProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provisionalDirectory = _options.JobRootDirectory ?? string.Empty;
        string root;
        try
        {
            root = ResolveJobRoot();
            provisionalDirectory = root;
        }
        catch (Exception ex)
        {
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                provisionalDirectory,
                "invalid_job_root",
                ex.Message);
        }

        if (Volatile.Read(ref _disposed) != 0)
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                provisionalDirectory,
                "client_disposed",
                "The backtest worker client has been disposed.");
        if (ct.IsCancellationRequested)
            return Failure(
                BacktestTerminalStatus.Cancelled,
                request.JobId,
                provisionalDirectory,
                "cancelled",
                "The job was cancelled before launch.");

        try
        {
            BacktestProtocolValidator.Validate(request);
            ValidateClientOptions();
            ValidateStrategyOptions(request);
        }
        catch (BacktestProtocolException ex)
        {
            return Failure(BacktestTerminalStatus.ProtocolError, request.JobId, provisionalDirectory, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Failure(BacktestTerminalStatus.StartFailed, request.JobId, provisionalDirectory, "invalid_client_options", ex.Message);
        }

        if (!BacktestWorkerExecutableResolver.TryResolve(_options, out var launch, out var resolutionError))
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                provisionalDirectory,
                "worker_not_found",
                resolutionError!);

        using var timeoutCts = new CancellationTokenSource(EffectiveTimeout(request));
        using var prelaunchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var prelaunchCt = prelaunchCts.Token;

        string jobDirectory;
        string requestPath;
        byte[] requestBytes;
        var stagingDirectory = string.Empty;
        IReadOnlyList<BacktestLoadedAssemblyFingerprint>? expectedStrategyClosure = null;
        try
        {
            Directory.CreateDirectory(root);
            var cleaned = AbandonedWorkerStagingCleaner.Cleanup(root, _options.AbandonedStagingAge, DateTime.UtcNow);
            if (cleaned > 0)
                _logger.LogInformation("Removed {Count} abandoned backtest worker staging directories.", cleaned);
            jobDirectory = BoundedJobDirectory(root, request.JobId);
            if (Directory.Exists(jobDirectory) || File.Exists(jobDirectory))
                throw new IOException($"Job '{request.JobId}' already exists under '{root}'.");
            stagingDirectory = CreateExclusiveStagingJobDirectory(root);
            provisionalDirectory = stagingDirectory;

            if (request.Strategy.Source == BacktestStrategySource.InstalledBundle)
            {
                expectedStrategyClosure = await PrepareStrategyImageAsync(
                        stagingDirectory,
                        request,
                        prelaunchCt)
                    .ConfigureAwait(false);
            }

            requestBytes = BacktestProtocolJson.SerializeToUtf8Bytes(request, writeIndented: true);
            if (requestBytes.LongLength > BacktestProtocolLimits.MaxRequestBytes)
                throw new InvalidDataException(
                    $"The serialized request exceeds {BacktestProtocolLimits.MaxRequestBytes} bytes.");
            await WriteNewFileAsync(
                Path.Combine(stagingDirectory, BacktestJobFiles.Request),
                requestBytes,
                prelaunchCt).ConfigureAwait(false);
            Directory.Move(stagingDirectory, jobDirectory);
            stagingDirectory = string.Empty;
            EnsureNoReparseComponentsUnder(root, jobDirectory);
            provisionalDirectory = jobDirectory;
            requestPath = Path.Combine(jobDirectory, BacktestJobFiles.Request);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failure(
                BacktestTerminalStatus.Cancelled,
                request.JobId,
                provisionalDirectory,
                "cancelled",
                "The job was cancelled before launch.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return Failure(
                BacktestTerminalStatus.TimedOut,
                request.JobId,
                provisionalDirectory,
                "worker_timeout",
                "The job exceeded its host-side timeout during preparation.");
        }
        catch (BacktestProtocolException ex)
        {
            return Failure(
                BacktestTerminalStatus.ProtocolError,
                request.JobId,
                provisionalDirectory,
                ex.Code,
                ex.Message);
        }
        catch (StrategyBundleStoreException ex)
        {
            return Failure(
                BacktestTerminalStatus.ProtocolError,
                request.JobId,
                provisionalDirectory,
                BundleStoreErrorCode(ex.Error),
                ex.Message);
        }
        catch (Exception ex)
        {
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                provisionalDirectory,
                "job_directory_failure",
                ex.Message);
        }
        finally
        {
            TryDeleteOwnedPrelaunchDirectory(root, stagingDirectory);
        }

        var launchGateName = $"Local\\DaxAlgo.Backtest.Worker.{Guid.NewGuid():N}";
        EventWaitHandle launchGate;
        try
        {
            launchGate = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                launchGateName,
                out var createdNew);
            if (!createdNew)
                throw new InvalidOperationException("The unique worker launch gate already existed.");
        }
        catch (Exception ex)
        {
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                jobDirectory,
                "launch_gate_failure",
                $"Unable to create the worker launch gate: {ex.Message}");
        }
        using var launchGateLease = launchGate;

        var startInfo = BuildStartInfo(launch!, requestPath, launchGateName);
        if (Volatile.Read(ref _disposed) != 0)
            return Failure(
                BacktestTerminalStatus.Cancelled,
                request.JobId,
                jobDirectory,
                "client_disposed",
                "The backtest worker client was disposed before launch.");
        var processGuard = new JobObjectProcessGuard();
        if (!processGuard.IsConfigured)
        {
            processGuard.Dispose();
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                jobDirectory,
                "job_object_unavailable",
                "Windows Job Object creation or KILL_ON_JOB_CLOSE configuration failed; the worker was not launched.");
        }
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            processGuard.Dispose();
            process?.Dispose();
            return Failure(
                BacktestTerminalStatus.StartFailed,
                request.JobId,
                jobDirectory,
                "worker_start_failed",
                $"Unable to launch '{launch!.ResolvedWorkerPath}': {ex.Message}");
        }

        using (process)
        {
            using var activeWorker = new ActiveWorker(process, processGuard);
            _active[process.Id] = activeWorker;
            var assigned = activeWorker.TryAssign();
            if (!assigned)
            {
                var disposedDuringLaunch = Volatile.Read(ref _disposed) != 0;
                if (disposedDuringLaunch) _disposedProcesses.TryAdd(process.Id, 0);
                activeWorker.Dispose();
                TryKillTree(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { /* The explicit failure below records that fail-closed ownership was unavailable. */ }
                _active.TryRemove(process.Id, out _);
                _disposedProcesses.TryRemove(process.Id, out _);
                return Failure(
                    disposedDuringLaunch ? BacktestTerminalStatus.Cancelled : BacktestTerminalStatus.StartFailed,
                    request.JobId,
                    jobDirectory,
                    disposedDuringLaunch ? "client_disposed" : "job_assignment_failed",
                    disposedDuringLaunch
                        ? "The backtest worker client was disposed during launch."
                        : "The worker could not be assigned to its configured Windows Job Object and was terminated.",
                    SafeExitCode(process));
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                _disposedProcesses.TryAdd(process.Id, 0);
                TryKillTree(process);
                activeWorker.Dispose();
            }
            else
            {
                try
                {
                    if (!launchGate.Set()) throw new InvalidOperationException("The launch gate could not be signaled.");
                }
                catch (Exception ex)
                {
                    activeWorker.Dispose();
                    TryKillTree(process);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch { }
                    _active.TryRemove(process.Id, out _);
                    return Failure(
                        BacktestTerminalStatus.StartFailed,
                        request.JobId,
                        jobDirectory,
                        "launch_gate_failure",
                        $"The assigned worker could not be released from its launch gate: {ex.Message}",
                        SafeExitCode(process));
                }
            }
            _logger.LogInformation(
                "Backtest worker started: job={JobId} pid={Pid} path={Path}",
                request.JobId,
                process.Id,
                launch!.ResolvedWorkerPath);

            var progressChannel = Channel.CreateBounded<BacktestJobProgress>(new BoundedChannelOptions(_options.ProgressBufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            var protocolFailure = new TaskCompletionSource<BacktestJobError>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resourceFailure = new TaskCompletionSource<BacktestJobError>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminationReason = (int)TerminationReason.None;

            void Terminate(TerminationReason reason)
            {
                if (process.HasExited) return;
                if (Interlocked.CompareExchange(ref terminationReason, (int)reason, (int)TerminationReason.None)
                    != (int)TerminationReason.None) return;
                activeWorker.Dispose();
                TryKillTree(process);
            }

            using var userRegistration = ct.Register(() => Terminate(TerminationReason.Cancelled));
            using var timeoutRegistration = timeoutCts.Token.Register(() => Terminate(TerminationReason.TimedOut));
            using var monitorCts = new CancellationTokenSource();

            var progressReadTask = ReadProgressAsync(
                process.StandardOutput,
                request,
                progressChannel.Writer,
                protocolFailure);
            var progressDispatchTask = DispatchProgressAsync(progressChannel.Reader, progress);
            var standardErrorTask = ReadBoundedTextAsync(
                process.StandardError,
                _options.MaxCapturedStandardErrorCharacters);
            var memoryTask = MonitorMemoryAsync(process, request.Limits.MaxWorkingSetBytes, resourceFailure, monitorCts.Token);
            var exitTask = process.WaitForExitAsync();

            var first = await Task.WhenAny(exitTask, protocolFailure.Task, resourceFailure.Task).ConfigureAwait(false);
            if (first == protocolFailure.Task) Terminate(TerminationReason.ProtocolError);
            if (first == resourceFailure.Task) Terminate(TerminationReason.ResourceLimit);

            try { await exitTask.ConfigureAwait(false); }
            catch { /* Exit classification below remains explicit. */ }
            // Closing this run's Job Object also kills a descendant that outlived a normally
            // exiting worker and closes inherited stdout/stderr handles before the bounded drain.
            activeWorker.Dispose();
            monitorCts.Cancel();
            try { await memoryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (resourceFailure.Task.IsCompletedSuccessfully)
                Interlocked.CompareExchange(
                    ref terminationReason,
                    (int)TerminationReason.ResourceLimit,
                    (int)TerminationReason.None);
            try { await progressReadTask.ConfigureAwait(false); }
            catch { }
            if (protocolFailure.Task.IsCompletedSuccessfully)
                Interlocked.CompareExchange(
                    ref terminationReason,
                    (int)TerminationReason.ProtocolError,
                    (int)TerminationReason.None);
            progressChannel.Writer.TryComplete();
            try { await progressDispatchTask.ConfigureAwait(false); }
            catch { }
            string standardError;
            try { standardError = await standardErrorTask.ConfigureAwait(false); }
            catch (Exception ex) { standardError = $"Unable to capture worker stderr: {ex.Message}"; }

            _active.TryRemove(process.Id, out _);
            if (_disposedProcesses.TryRemove(process.Id, out _))
            {
                return Failure(
                    BacktestTerminalStatus.Cancelled,
                    request.JobId,
                    jobDirectory,
                    "client_disposed",
                    "The worker was terminated because its client was disposed.",
                    SafeExitCode(process),
                    standardError);
            }
            var reason = (TerminationReason)Volatile.Read(ref terminationReason);
            if (reason != TerminationReason.None)
            {
                var (status, code, message) = reason switch
                {
                    TerminationReason.Cancelled => (BacktestTerminalStatus.Cancelled, "cancelled", "The worker job was cancelled."),
                    TerminationReason.TimedOut => (BacktestTerminalStatus.TimedOut, "worker_timeout", "The worker exceeded its host-side timeout."),
                    TerminationReason.ResourceLimit => (BacktestTerminalStatus.ResourceLimitExceeded, "memory_limit_exceeded", resourceFailure.Task.IsCompletedSuccessfully ? resourceFailure.Task.Result.Message : "The worker exceeded its memory limit."),
                    TerminationReason.ProtocolError => (BacktestTerminalStatus.ProtocolError, protocolFailure.Task.IsCompletedSuccessfully ? protocolFailure.Task.Result.Code : "invalid_progress", protocolFailure.Task.IsCompletedSuccessfully ? protocolFailure.Task.Result.Message : "The worker emitted invalid progress."),
                    _ => throw new ArgumentOutOfRangeException(),
                };
                return Failure(status, request.JobId, jobDirectory, code, message, SafeExitCode(process), standardError);
            }

            try
            {
                using var verificationCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var terminal = await LoadAndVerifyResultAsync(
                    jobDirectory,
                    request,
                    requestBytes,
                    expectedStrategyClosure,
                    verificationCts.Token).ConfigureAwait(false);
                var exitCode = SafeExitCode(process);
                if (terminal.Manifest.TerminalStatus == BacktestTerminalStatus.Succeeded && exitCode != 0)
                    throw new BacktestProtocolException(
                        "exit_status_mismatch",
                        $"The worker published success but exited with code {exitCode}.");

                return new BacktestJobOutcome(
                    terminal.Manifest.TerminalStatus,
                    request.JobId,
                    jobDirectory,
                    terminal.Manifest,
                    terminal.Report,
                    terminal.Manifest.Error,
                    exitCode,
                    string.IsNullOrWhiteSpace(standardError) ? null : standardError);
            }
            catch (BacktestProtocolException ex)
            {
                var exitCode = SafeExitCode(process);
                var status = exitCode == 0
                    ? BacktestTerminalStatus.ProtocolError
                    : BacktestTerminalStatus.WorkerCrashed;
                return Failure(status, request.JobId, jobDirectory, ex.Code, ex.Message, exitCode, standardError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Failure(
                    BacktestTerminalStatus.Cancelled,
                    request.JobId,
                    jobDirectory,
                    "cancelled",
                    "The job was cancelled while verifying its result.",
                    SafeExitCode(process),
                    standardError);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return Failure(
                    BacktestTerminalStatus.TimedOut,
                    request.JobId,
                    jobDirectory,
                    "worker_timeout",
                    "The job exceeded its host-side timeout while verifying the result.",
                    SafeExitCode(process),
                    standardError);
            }
            catch (Exception ex)
            {
                return Failure(
                    BacktestTerminalStatus.WorkerCrashed,
                    request.JobId,
                    jobDirectory,
                    "missing_or_invalid_result",
                    ex.Message,
                    SafeExitCode(process),
                    standardError);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var active in _active.Values)
        {
            _disposedProcesses.TryAdd(active.Process.Id, 0);
            active.Dispose();
            TryKillTree(active.Process);
        }
    }

    private ProcessStartInfo BuildStartInfo(
        BacktestWorkerLaunch launch,
        string requestPath,
        string launchGateName)
    {
        var info = new ProcessStartInfo
        {
            FileName = launch.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launch.ResolvedWorkerPath) ?? AppContext.BaseDirectory,
        };
        foreach (var argument in launch.PrefixArguments) info.ArgumentList.Add(argument);
        foreach (var argument in _options.WorkerArguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--request");
        info.ArgumentList.Add(requestPath);
        info.ArgumentList.Add("--launch-gate");
        info.ArgumentList.Add(launchGateName);
        return info;
    }

    private async Task ReadProgressAsync(
        StreamReader reader,
        BacktestJobRequest request,
        ChannelWriter<BacktestJobProgress> writer,
        TaskCompletionSource<BacktestJobError> failure)
    {
        long lastSequence = 0;
        var received = 0;
        try
        {
            while (true)
            {
                var line = await ReadBoundedLineAsync(reader, _options.MaxProgressLineCharacters).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var item = BacktestProtocolJson.Deserialize<BacktestJobProgress>(line);
                if (item.ProtocolVersion != BacktestProtocolVersions.Current)
                    throw new BacktestProtocolException("invalid_progress_version", "Worker progress used an unsupported protocol version.");
                if (!string.Equals(item.JobId, request.JobId, StringComparison.Ordinal))
                    throw new BacktestProtocolException("invalid_progress_job", "Worker progress named a different job.");
                if (item.Sequence <= lastSequence)
                    throw new BacktestProtocolException("invalid_progress_sequence", "Worker progress sequence was not strictly increasing.");
                if (++received > request.Limits.MaxProgressMessages + 8)
                    throw new BacktestProtocolException("progress_limit_exceeded", "Worker progress exceeded the bounded request limit.");
                lastSequence = item.Sequence;
                writer.TryWrite(item);
            }
        }
        catch (Exception ex)
        {
            var error = ex is BacktestProtocolException protocol
                ? new BacktestJobError(protocol.Code, protocol.Message)
                : new BacktestJobError("invalid_progress", $"Malformed worker progress: {ex.Message}");
            failure.TrySetResult(error);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DispatchProgressAsync(
        ChannelReader<BacktestJobProgress> reader,
        IProgress<BacktestJobProgress>? progress)
    {
        if (progress is null)
        {
            await foreach (var _ in reader.ReadAllAsync().ConfigureAwait(false)) { }
            return;
        }

        await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { progress.Report(item); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backtest progress observer failed for job {JobId}.", item.JobId);
            }
        }
    }

    private static async Task MonitorMemoryAsync(
        Process process,
        long maxWorkingSetBytes,
        TaskCompletionSource<BacktestJobError> failure,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (process.HasExited) return;
            try
            {
                process.Refresh();
                if (process.WorkingSet64 <= maxWorkingSetBytes) continue;
                failure.TrySetResult(new BacktestJobError(
                    "memory_limit_exceeded",
                    $"Worker working set {process.WorkingSet64} exceeded the limit {maxWorkingSetBytes}."));
                return;
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                return;
            }
        }
    }

    private static async Task<(BacktestResultManifest Manifest, BacktestReport? Report)> LoadAndVerifyResultAsync(
        string jobDirectory,
        BacktestJobRequest request,
        byte[] requestBytes,
        IReadOnlyList<BacktestLoadedAssemblyFingerprint>? expectedStrategyClosure,
        CancellationToken ct)
    {
        var manifestPath = Path.Combine(jobDirectory, BacktestJobFiles.ResultManifest);
        var hashPath = Path.Combine(jobDirectory, BacktestJobFiles.ResultManifestHash);
        if (!File.Exists(manifestPath) || !File.Exists(hashPath))
            throw new BacktestProtocolException("missing_result_manifest", "The worker did not publish a complete result manifest.");
        EnsureNoReparseComponentsUnder(jobDirectory, manifestPath);
        EnsureNoReparseComponentsUnder(jobDirectory, hashPath);

        var manifestBytes = await ReadBoundedFileAsync(
            manifestPath,
            BacktestProtocolLimits.MaxRequestBytes,
            ct).ConfigureAwait(false);
        if (manifestBytes.Length == 0)
            throw new BacktestProtocolException("invalid_result_manifest", "The result manifest is empty.");
        var hashBytes = await ReadBoundedFileAsync(hashPath, 128, ct).ConfigureAwait(false);
        var expectedHash = Encoding.ASCII.GetString(hashBytes).Trim();
        if (!BacktestProtocolHash.IsSha256(expectedHash) ||
            !string.Equals(expectedHash, BacktestProtocolHash.ComputeSha256(manifestBytes), StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("result_manifest_hash_mismatch", "The result manifest SHA-256 did not match.");

        BacktestResultManifest manifest;
        try
        {
            manifest = BacktestProtocolJson.Deserialize<BacktestResultManifest>(manifestBytes);
        }
        catch (JsonException ex)
        {
            throw new BacktestProtocolException("invalid_result_manifest", "The result manifest JSON was invalid.", ex);
        }
        if (manifest.ProtocolVersion != BacktestProtocolVersions.Current)
            throw new BacktestProtocolException("invalid_result_version", "The result manifest protocol version is unsupported.");
        if (!Enum.IsDefined(manifest.TerminalStatus) ||
            manifest.TerminalStatus is BacktestTerminalStatus.StartFailed or BacktestTerminalStatus.WorkerCrashed)
            throw new BacktestProtocolException("invalid_terminal_status", "The result manifest contained an unsupported worker terminal status.");
        if (!string.Equals(manifest.JobId, request.JobId, StringComparison.Ordinal))
            throw new BacktestProtocolException("invalid_result_job", "The result manifest named a different job.");
        if (!string.Equals(
                manifest.RequestSha256,
                BacktestProtocolHash.ComputeSha256(requestBytes),
                StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("request_hash_mismatch", "The result was produced from different request bytes.");
        if (!string.Equals(manifest.EngineVersion, request.EngineVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.EngineVersion, BacktestProtocolVersions.ManagedEngine, StringComparison.Ordinal))
            throw new BacktestProtocolException("result_engine_version_mismatch", "The result engine version did not match the request.");
        if (!string.Equals(manifest.SdkVersion, request.SdkVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.SdkVersion, BacktestProtocolVersions.Sdk, StringComparison.Ordinal))
            throw new BacktestProtocolException("result_sdk_version_mismatch", "The result SDK version did not match the request.");
        if (!string.Equals(manifest.StrategyContractVersion, request.StrategyContractVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.StrategyContractVersion, BacktestProtocolVersions.StrategyContract, StringComparison.Ordinal))
            throw new BacktestProtocolException(
                "result_strategy_contract_version_mismatch",
                "The result strategy-contract version did not match the request.");
        if (!BacktestProtocolHash.IsSha256(manifest.HostEngineAssemblySha256))
            throw new BacktestProtocolException(
                "invalid_result_host_engine_hash",
                "The result host-engine hash was invalid.");
        if (manifest.TerminalStatus == BacktestTerminalStatus.Succeeded &&
            !string.Equals(
                manifest.HostEngineAssemblySha256,
                request.ExpectedHostEngineAssemblySha256,
                StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException(
                "result_host_engine_hash_mismatch",
                "The result host-engine hash did not match the requested deployment.");
        if (!string.Equals(manifest.ParametersSha256, request.ParametersSha256, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("result_parameters_mismatch", "The result parameter hash did not match the request.");
        if (!string.Equals(manifest.StrategyId, request.Strategy.Id, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("result_strategy_mismatch", "The result strategy id did not match the request.");
        if (!BacktestProtocolHash.IsSha256(manifest.StrategyAssemblySha256))
            throw new BacktestProtocolException("invalid_result_strategy_hash", "The result strategy assembly hash was invalid.");
        if (request.Strategy.Source == BacktestStrategySource.Native &&
            (manifest.StrategyContentRootSha256 is not null ||
             manifest.StrategyArchiveSha256 is not null ||
             manifest.StrategyTrustEvidence is not null))
        {
            throw new BacktestProtocolException(
                "result_strategy_source_confusion",
                "A native strategy result carried installed-bundle provenance.");
        }
        if (manifest.TerminalStatus == BacktestTerminalStatus.Succeeded &&
            request.Strategy.ExpectedAssemblySha256 is { } expectedAssembly &&
            !string.Equals(manifest.StrategyAssemblySha256, expectedAssembly, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("result_strategy_hash_mismatch", "The result strategy assembly hash did not match the request.");
        if (manifest.TerminalStatus == BacktestTerminalStatus.Succeeded &&
            request.Strategy.Source == BacktestStrategySource.InstalledBundle)
        {
            var bundle = request.Strategy.InstalledBundle!;
            if (!string.Equals(
                    manifest.StrategyContentRootSha256,
                    bundle.ContentRootSha256,
                    StringComparison.Ordinal))
                throw new BacktestProtocolException(
                    "result_bundle_content_root_mismatch",
                    "The result bundle content root did not match the request.");
            if (!string.Equals(
                    manifest.StrategyArchiveSha256,
                    bundle.ArchiveSha256,
                    StringComparison.Ordinal))
                throw new BacktestProtocolException(
                    "result_bundle_archive_mismatch",
                    "The result bundle archive identity did not match the request.");
            if (manifest.StrategyTrustEvidence != bundle.TrustEvidence)
                throw new BacktestProtocolException(
                    "result_bundle_trust_evidence_mismatch",
                    "The result bundle trust evidence did not match the verified installation.");
            var closure = manifest.StrategyAssemblyClosure
                          ?? throw new BacktestProtocolException(
                              "missing_result_strategy_closure",
                              "The bundle result omitted its loaded assembly closure.");
            if (expectedStrategyClosure is null ||
                closure.Count != expectedStrategyClosure.Count ||
                closure.Count == 0 ||
                !string.Equals(
                    closure[0].Sha256,
                    manifest.StrategyAssemblySha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new BacktestProtocolException(
                    "invalid_result_strategy_closure",
                    "The verified engine closure does not match the staged strategy image.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < closure.Count; index++)
            {
                var assembly = closure[index];
                var expected = expectedStrategyClosure[index];
                if (string.IsNullOrWhiteSpace(assembly.Name) ||
                    !BacktestProtocolHash.IsSha256(assembly.Sha256) ||
                    !names.Add(assembly.Name) ||
                    !string.Equals(assembly.Name, expected.Name, StringComparison.Ordinal) ||
                    !string.Equals(assembly.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new BacktestProtocolException(
                        "invalid_result_strategy_closure",
                        "The verified engine closure differs from the exact staged strategy image.");
            }
        }
        var expectedInputHash = request.Input.Kind == BacktestInputKind.Parquet
            ? request.Input.Sha256!
            : BacktestProtocolHash.ComputeSha256(BacktestProtocolJson.Serialize(request.Input));
        if (!string.Equals(manifest.InputSha256, expectedInputHash, StringComparison.OrdinalIgnoreCase) &&
            !(manifest.TerminalStatus != BacktestTerminalStatus.Succeeded &&
              string.Equals(manifest.InputSha256, BacktestProtocolHash.UnknownSha256, StringComparison.Ordinal)))
            throw new BacktestProtocolException("result_input_hash_mismatch", "The result input hash did not match the request.");
        if (string.IsNullOrWhiteSpace(manifest.EngineFingerprint) || string.IsNullOrWhiteSpace(manifest.BackendFingerprint))
            throw new BacktestProtocolException("missing_result_fingerprint", "The result omitted its engine or backend fingerprint.");
        if (manifest.CompletedUtc < manifest.StartedUtc)
            throw new BacktestProtocolException("invalid_result_time", "The result completion time preceded its start time.");

        var artifacts = manifest.Artifacts
                        ?? throw new BacktestProtocolException("missing_artifact_list", "The result manifest omitted its artifact list.");
        var artifactPaths = new HashSet<string>(StringComparer.Ordinal);
        var artifactKinds = new HashSet<BacktestArtifactKind>();
        foreach (var artifact in artifacts)
        {
            if (artifact is null)
                throw new BacktestProtocolException("invalid_artifact", "The result manifest contained a null artifact descriptor.");
            if (!Enum.IsDefined(artifact.Kind) || artifact.Kind != BacktestArtifactKind.Report)
                throw new BacktestProtocolException("unsupported_artifact_kind", "The result manifest contained an unsupported artifact kind.");
            if (string.IsNullOrWhiteSpace(artifact.RelativePath))
                throw new BacktestProtocolException("invalid_artifact_path", "The result manifest contained an empty artifact path.");
            if (!artifactPaths.Add(artifact.RelativePath) || !artifactKinds.Add(artifact.Kind))
                throw new BacktestProtocolException("duplicate_artifact", "The result manifest contained duplicate artifact identities.");
        }

        if (manifest.TerminalStatus == BacktestTerminalStatus.Succeeded)
        {
            if (artifacts.Count != 1 || artifacts[0].Kind != BacktestArtifactKind.Report ||
                !string.Equals(
                    artifacts[0].RelativePath,
                    $"{BacktestJobFiles.ArtifactDirectory}/{BacktestJobFiles.ReportArtifact}",
                    StringComparison.Ordinal))
                throw new BacktestProtocolException(
                    "invalid_success_artifacts",
                    "A successful P2 result must contain exactly the canonical report artifact.");
            if (manifest.Error is not null)
                throw new BacktestProtocolException("unexpected_result_error", "A successful result contained an error envelope.");
        }
        else
        {
            if (artifacts.Count != 0)
                throw new BacktestProtocolException("unexpected_failure_artifacts", "A failed result cannot publish completed artifacts.");
            if (manifest.Error is null || string.IsNullOrWhiteSpace(manifest.Error.Code) ||
                string.IsNullOrWhiteSpace(manifest.Error.Message))
                throw new BacktestProtocolException("missing_result_error", "A failed result did not contain a complete error envelope.");
        }

        BacktestReport? report = null;
        long totalArtifactBytes = 0;
        foreach (var artifact in artifacts)
        {
            if (!BacktestProtocolHash.IsSha256(artifact.Sha256) || artifact.LengthBytes < 0)
                throw new BacktestProtocolException("invalid_artifact_identity", $"Result artifact '{artifact.RelativePath}' identity was invalid.");
            if (artifact.SchemaVersion != BacktestProtocolVersions.ReportArtifact)
                throw new BacktestProtocolException("unsupported_report_schema", "The report artifact schema is unsupported.");
            var path = ResolveArtifactPath(jobDirectory, artifact.RelativePath);
            if (!File.Exists(path))
                throw new BacktestProtocolException("missing_result_artifact", $"Result artifact '{artifact.RelativePath}' is missing.");
            EnsureNoReparseComponentsUnder(jobDirectory, path);

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != artifact.LengthBytes)
                throw new BacktestProtocolException("artifact_length_mismatch", $"Result artifact '{artifact.RelativePath}' length did not match.");
            if (stream.Length > request.Limits.MaxArtifactBytes - totalArtifactBytes)
                throw new BacktestProtocolException("artifact_limit_exceeded", "Result artifacts exceeded the request limit.");
            totalArtifactBytes += stream.Length;
            var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BacktestProtocolException("artifact_hash_mismatch", $"Result artifact '{artifact.RelativePath}' SHA-256 did not match.");

            stream.Position = 0;
            BacktestReportArtifact payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<BacktestReportArtifact>(
                              stream,
                              BacktestProtocolJson.Options,
                              ct).ConfigureAwait(false)
                          ?? throw new BacktestProtocolException(
                              "empty_report_artifact",
                              "The report artifact was empty.");
            }
            catch (JsonException ex)
            {
                throw new BacktestProtocolException("invalid_report_artifact", "The report artifact JSON was invalid.", ex);
            }
            if (payload.SchemaVersion != BacktestProtocolVersions.ReportArtifact)
                throw new BacktestProtocolException("unsupported_report_schema", "The report artifact schema is unsupported.");
            report = payload.ToReport();
        }

        if (manifest.TerminalStatus == BacktestTerminalStatus.Succeeded && report is null)
            throw new BacktestProtocolException("missing_report_artifact", "A successful result did not contain a report artifact.");
        return (manifest, report);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 0 or > int.MaxValue || stream.Length > maxBytes)
            throw new BacktestProtocolException(
                "bounded_file_limit_exceeded",
                $"'{Path.GetFileName(path)}' exceeded its {maxBytes}-byte limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 512));
        var one = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(one.AsMemory()).ConfigureAwait(false);
            if (count == 0) return builder.Length == 0 ? null : builder.ToString();
            if (one[0] == '\n') return builder.ToString();
            if (one[0] == '\r') continue;
            if (builder.Length >= maxCharacters)
                throw new BacktestProtocolException("progress_line_too_large", "A worker progress line exceeded the configured limit.");
            builder.Append(one[0]);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(TextReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[1024];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = maxCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[stderr truncated]");
        return builder.ToString();
    }

    private TimeSpan EffectiveTimeout(BacktestJobRequest request)
    {
        var timeout = TimeSpan.FromMilliseconds(request.Limits.MaxWallClockMilliseconds);
        if (_options.DefaultTimeout > TimeSpan.Zero && _options.DefaultTimeout < timeout)
            timeout = _options.DefaultTimeout;
        if (request.DeadlineUtc is { } deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < timeout) timeout = remaining;
        }
        return timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : timeout;
    }

    private void ValidateClientOptions()
    {
        if (_options.DefaultTimeout <= TimeSpan.Zero)
            throw new BacktestProtocolException("invalid_client_timeout", "DefaultTimeout must be positive.");
        if (_options.ProgressBufferCapacity is < 1 or > 4096)
            throw new BacktestProtocolException("invalid_progress_buffer", "ProgressBufferCapacity must be between 1 and 4096.");
        if (_options.MaxProgressLineCharacters is < 256 or > BacktestProtocolLimits.MaxProgressLineCharacters)
            throw new BacktestProtocolException("invalid_progress_line_limit", "MaxProgressLineCharacters is outside the protocol limits.");
        if (_options.MaxCapturedStandardErrorCharacters is < 1024 or > 1024 * 1024)
            throw new BacktestProtocolException("invalid_stderr_limit", "MaxCapturedStandardErrorCharacters must be between 1024 and 1048576.");
        if (_options.AbandonedStagingAge < TimeSpan.FromMinutes(5))
            throw new BacktestProtocolException("invalid_staging_age", "AbandonedStagingAge must be at least five minutes.");
    }

    private void ValidateStrategyOptions(BacktestJobRequest request)
    {
        if (request.Strategy.Source != BacktestStrategySource.InstalledBundle) return;
        if (string.IsNullOrWhiteSpace(_options.StrategyBundleStoreRoot) ||
            _options.StrategyBundlePolicy is null)
        {
            throw new BacktestProtocolException(
                "bundle_store_unavailable",
                "Installed-bundle jobs require a strategy bundle store root and current trust policy.");
        }
    }

    private async Task<IReadOnlyList<BacktestLoadedAssemblyFingerprint>> PrepareStrategyImageAsync(
        string jobDirectory,
        BacktestJobRequest request,
        CancellationToken ct)
    {
        var reference = request.Strategy.InstalledBundle!;
        ct.ThrowIfCancellationRequested();
        var verificationTask = Task.Run(() =>
        {
            var store = new StrategyBundleStore(_options.StrategyBundleStoreRoot!);
            return store.VerifyInstallation(
                reference.ContentRootSha256,
                reference.ArchiveSha256,
                _options.StrategyBundlePolicy!);
        });
        StrategyBundleInstallation installation;
        try
        {
            installation = await verificationTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = ObserveBackgroundFailureAsync(verificationTask);
            throw;
        }
        var manifest = installation.Manifest;
        if (!string.Equals(manifest.Identity.Id, request.Strategy.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.Identity.PublisherId, reference.PublisherId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Identity.Version, reference.StrategyVersion, StringComparison.Ordinal))
        {
            throw new BacktestProtocolException(
                "bundle_installation_identity_mismatch",
                "The selected immutable installation does not match the requested strategy identity.");
        }
        if (reference.TrustEvidence != ToProtocolTrustEvidence(installation.Receipt.PublisherSignature))
        {
            throw new BacktestProtocolException(
                "bundle_installation_trust_evidence_mismatch",
                "The selected immutable installation does not match the requested publisher trust evidence.");
        }

        var closure = DaxStrategyBundle.ResolveEngineClosure(manifest);
        if (!string.Equals(
                closure[0].Sha256,
                request.Strategy.ExpectedAssemblySha256,
                StringComparison.Ordinal))
        {
            throw new BacktestProtocolException(
                "bundle_installation_engine_mismatch",
                "The selected immutable installation does not match the requested engine hash.");
        }

        var imageRoot = Path.Combine(jobDirectory, BacktestJobFiles.StrategyDirectory);
        Directory.CreateDirectory(imageRoot);
        var manifestSource = installation.ManifestPath;
        var manifestBytes = await ReadAndVerifyFileAsync(
            installation.ObjectDirectory,
            manifestSource,
            exactLength: null,
            maximumLength: StrategyBundleLimitOptions.Default.MaxManifestBytes,
            expectedSha256: reference.ContentRootSha256,
            ct: ct).ConfigureAwait(false);
        await WriteNewFileAsync(
            Path.Combine(imageRoot, BacktestJobFiles.StrategyManifest),
            manifestBytes,
            ct).ConfigureAwait(false);

        foreach (var assembly in closure)
        {
            var source = ResolveContainedPath(installation.ObjectDirectory, assembly.Path);
            var bytes = await ReadAndVerifyFileAsync(
                installation.ObjectDirectory,
                source,
                exactLength: assembly.Length,
                maximumLength: assembly.Length,
                expectedSha256: assembly.Sha256,
                ct: ct).ConfigureAwait(false);
            var destination = ResolveContainedPath(imageRoot, assembly.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await WriteNewFileAsync(destination, bytes, ct).ConfigureAwait(false);
        }

        return closure
            .Select(static assembly => new BacktestLoadedAssemblyFingerprint(
                assembly.Name,
                assembly.Sha256))
            .ToArray();
    }

    private static string BundleStoreErrorCode(StrategyBundleStoreError error) => error switch
    {
        StrategyBundleStoreError.IncompatibleSdk => "bundle_store_incompatible_sdk",
        StrategyBundleStoreError.IncompatibleHost => "bundle_store_incompatible_host",
        StrategyBundleStoreError.SignatureRejected => "bundle_store_signature_rejected",
        StrategyBundleStoreError.InstallationNotFound => "bundle_store_installation_not_found",
        StrategyBundleStoreError.CorruptInstallation => "bundle_store_corrupt_installation",
        _ => "bundle_store_failure",
    };

    private static async Task ObserveBackgroundFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The caller already returned cancellation/timeout; observe late verification failure. */ }
    }

    private static BacktestBundleTrustEvidence ToProtocolTrustEvidence(
        StrategyBundleSignatureEvidence evidence) => evidence.Status switch
    {
        StrategyBundleSignatureStatus.Missing => new BacktestBundleTrustEvidence
        {
            Kind = BacktestBundleTrustKind.UnsignedLocalDevelopment,
        },
        StrategyBundleSignatureStatus.Verified => new BacktestBundleTrustEvidence
        {
            Kind = BacktestBundleTrustKind.VerifiedPublisher,
            PublisherKeyId = evidence.KeyId,
            PublisherKeyFingerprintSha256 = evidence.KeyFingerprintSha256,
        },
        _ => throw new BacktestProtocolException(
            "invalid_bundle_installation_trust_evidence",
            "The immutable installation contains publisher evidence that cannot be executed."),
    };

    private static async Task<byte[]> ReadAndVerifyFileAsync(
        string root,
        string path,
        long? exactLength,
        long maximumLength,
        string expectedSha256,
        CancellationToken ct)
    {
        EnsureNoReparseComponentsUnder(root, path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 0 || stream.Length > maximumLength || stream.Length > int.MaxValue ||
            exactLength is { } requiredLength && stream.Length != requiredLength)
            throw new BacktestProtocolException(
                "bundle_installation_length_mismatch",
                $"Installed strategy file '{Path.GetFileName(path)}' has the wrong length.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var actualHash = BacktestProtocolHash.ComputeSha256(bytes);
        if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
            throw new BacktestProtocolException(
                "bundle_installation_hash_mismatch",
                $"Installed strategy file '{Path.GetFileName(path)}' failed its SHA-256 check.");
        return bytes;
    }

    private static string ResolveContainedPath(string root, string portablePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            portablePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException(
                "invalid_bundle_installation_path",
                "An installed strategy path escaped its immutable content object.");
        return candidate;
    }

    private string ResolveJobRoot()
    {
        string root;
        if (!string.IsNullOrWhiteSpace(_options.JobRootDirectory))
        {
            root = Path.GetFullPath(_options.JobRootDirectory);
        }
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            root = Path.GetFullPath(Path.Combine(local, "DaxAlgo", "BacktestJobs"));
        }

        root = Path.TrimEndingDirectorySeparator(root);
        var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)
                             ?? throw new InvalidDataException("The job root has no filesystem root."));
        if (string.Equals(root, filesystemRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The job root cannot be a filesystem root.");
        return root;
    }

    private static string BoundedJobDirectory(string root, string jobId)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, jobId));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The job id escaped the configured job root.");
        return candidate;
    }

    private static string CreateExclusiveStagingJobDirectory(string root)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var staging = BoundedJobDirectory(root, $".staging-{Guid.NewGuid():N}");
            if (Directory.Exists(staging) || File.Exists(staging)) continue;
            Directory.CreateDirectory(staging);
            EnsureNoReparseComponentsUnder(root, staging);
            return staging;
        }
        throw new IOException("Could not allocate a unique backtest job staging directory.");
    }

    private static void TryDeleteOwnedPrelaunchDirectory(string root, string staging)
    {
        if (string.IsNullOrEmpty(staging)) return;
        try
        {
            if (!Directory.Exists(staging) ||
                !IsDirectChild(root, staging) ||
                !IsWorkerStagingName(Path.GetFileName(staging)) ||
                (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The normal age-bounded cleaner owns this exact random staging namespace.
        }
    }

    private static bool IsDirectChild(string parent, string candidate) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(candidate)),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerStagingName(string name)
    {
        const string prefix = ".staging-";
        return name.Length == prefix.Length + 32 &&
               name.StartsWith(prefix, StringComparison.Ordinal) &&
               name[prefix.Length..].All(Uri.IsHexDigit);
    }

    private static string ResolveArtifactPath(string jobDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            throw new BacktestProtocolException("invalid_artifact_path", "Result artifact paths must be relative.");
        var root = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("invalid_artifact_path", "A result artifact escaped the job directory.");
        return candidate;
    }

    private static void EnsureNoReparseComponentsUnder(string jobDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jobDirectory));
        var candidate = Path.GetFullPath(candidatePath);
        if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("invalid_result_path", "A result path escaped the job directory.");

        Inspect(root);
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".") return;
        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            Inspect(current);
        }

        static void Inspect(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new BacktestProtocolException(
                    "result_path_unavailable",
                    $"The result path component '{path}' could not be inspected.",
                    ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new BacktestProtocolException(
                    "result_reparse_point",
                    "Result paths cannot contain symbolic links, junctions, or other reparse points.");
        }
    }

    private static async Task WriteNewFileAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static BacktestJobOutcome Failure(
        BacktestTerminalStatus status,
        string jobId,
        string jobDirectory,
        string code,
        string message,
        int? exitCode = null,
        string? standardError = null) =>
        new(
            status,
            jobId,
            jobDirectory,
            Manifest: null,
            Report: null,
            Error: new BacktestJobError(code, message),
            WorkerExitCode: exitCode,
            WorkerStandardError: string.IsNullOrWhiteSpace(standardError) ? null : standardError);

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static void TryKillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* Job Object disposal remains the final Windows guard. */ }
    }

    private enum TerminationReason
    {
        None = 0,
        Cancelled = 1,
        TimedOut = 2,
        ResourceLimit = 3,
        ProtocolError = 4,
    }

    private sealed class ActiveWorker(Process process, JobObjectProcessGuard guard) : IDisposable
    {
        private int _disposed;

        public Process Process { get; } = process;

        public bool TryAssign() => guard.TryAssign(Process);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) guard.Dispose();
        }
    }
}
