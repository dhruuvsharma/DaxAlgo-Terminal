using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Worker;

internal sealed record WorkerStrategyFingerprint(
    string AssemblySha256,
    string? ContentRootSha256,
    string? ArchiveSha256,
    IReadOnlyList<BacktestLoadedAssemblyFingerprint> AssemblyClosure)
{
    public static WorkerStrategyFingerprint Unknown { get; } = new(
        BacktestProtocolHash.UnknownSha256,
        null,
        null,
        []);
}

/// <summary>Writes private staging files, moves artifacts into place, then publishes the manifest last.</summary>
internal sealed class WorkerArtifactPublisher(string jobDirectory, BacktestJobRequest request, string requestSha256)
{
    private readonly string _jobDirectory = Path.GetFullPath(jobDirectory);

    public async Task<BacktestResultManifest> PublishSuccessAsync(
        BacktestReport report,
        DateTime startedUtc,
        string engineAssemblySha256,
        WorkerStrategyFingerprint strategyFingerprint,
        string inputSha256,
        CancellationToken ct)
    {
        EnsureTerminalManifestAbsent();
        var staging = CreateStagingDirectory();
        try
        {
            var stagedReport = Path.Combine(staging, BacktestJobFiles.ReportArtifact);
            await WriteJsonFileAsync(
                stagedReport,
                BacktestReportArtifact.FromReport(report),
                ct).ConfigureAwait(false);
            var reportLength = new FileInfo(stagedReport).Length;
            if (reportLength > request.Limits.MaxArtifactBytes)
                throw new InvalidDataException(
                    $"The report artifact is {reportLength} bytes; the limit is {request.Limits.MaxArtifactBytes}.");

            var artifact = new BacktestArtifactDescriptor(
                BacktestArtifactKind.Report,
                BacktestProtocolVersions.ReportArtifact,
                $"{BacktestJobFiles.ArtifactDirectory}/{BacktestJobFiles.ReportArtifact}",
                reportLength,
                await BacktestProtocolHash.ComputeFileSha256Async(stagedReport, ct).ConfigureAwait(false));

            var artifactDirectory = BoundedPath(BacktestJobFiles.ArtifactDirectory);
            if (Directory.Exists(artifactDirectory) || File.Exists(artifactDirectory))
                throw new IOException($"The job artifact path already exists: {artifactDirectory}");
            Directory.Move(staging, artifactDirectory);
            staging = string.Empty;

            var manifest = CreateManifest(
                BacktestTerminalStatus.Succeeded,
                startedUtc,
                engineAssemblySha256,
                strategyFingerprint,
                inputSha256,
                [artifact],
                error: null);
            await PublishManifestAsync(manifest, ct).ConfigureAwait(false);
            return manifest;
        }
        finally
        {
            TryDeleteOwnedStaging(staging);
        }
    }

    public async Task<BacktestResultManifest> PublishFailureAsync(
        BacktestTerminalStatus status,
        BacktestJobError error,
        DateTime startedUtc,
        string engineAssemblySha256,
        WorkerStrategyFingerprint strategyFingerprint,
        string inputSha256,
        CancellationToken ct = default)
    {
        EnsureTerminalManifestAbsent();
        var manifest = CreateManifest(
            status,
            startedUtc,
            engineAssemblySha256,
            strategyFingerprint,
            inputSha256,
            [],
            error);
        await PublishManifestAsync(manifest, ct).ConfigureAwait(false);
        return manifest;
    }

    private BacktestResultManifest CreateManifest(
        BacktestTerminalStatus status,
        DateTime startedUtc,
        string engineAssemblySha256,
        WorkerStrategyFingerprint strategyFingerprint,
        string inputSha256,
        IReadOnlyList<BacktestArtifactDescriptor> artifacts,
        BacktestJobError? error) =>
        new()
        {
            JobId = request.JobId,
            TerminalStatus = status,
            StartedUtc = startedUtc,
            CompletedUtc = DateTime.UtcNow,
            RequestSha256 = requestSha256,
            EngineVersion = request.EngineVersion,
            SdkVersion = request.SdkVersion,
            StrategyContractVersion = request.StrategyContractVersion,
            EngineFingerprint = $"{typeof(Engine.BacktestEngine).Assembly.GetName().Version}+sha256:{engineAssemblySha256}",
            HostEngineAssemblySha256 = engineAssemblySha256,
            BackendFingerprint = $"managed-dotnet-{Environment.Version}-{RuntimeInformation.ProcessArchitecture}",
            StrategyId = request.Strategy.Id,
            StrategyAssemblySha256 = strategyFingerprint.AssemblySha256,
            StrategyContentRootSha256 = strategyFingerprint.ContentRootSha256,
            StrategyArchiveSha256 = strategyFingerprint.ArchiveSha256,
            StrategyTrustEvidence = request.Strategy.InstalledBundle?.TrustEvidence,
            StrategyAssemblyClosure = strategyFingerprint.AssemblyClosure,
            ParametersSha256 = request.ParametersSha256,
            InputSha256 = inputSha256,
            Artifacts = artifacts,
            Error = error,
        };

    private async Task PublishManifestAsync(BacktestResultManifest manifest, CancellationToken ct)
    {
        var manifestBytes = BacktestProtocolJson.SerializeToUtf8Bytes(manifest, writeIndented: true);
        var manifestHash = BacktestProtocolHash.ComputeSha256(manifestBytes);
        var nonce = Guid.NewGuid().ToString("N");
        var manifestTemp = BoundedPath($".{BacktestJobFiles.ResultManifest}.{nonce}.tmp");
        var hashTemp = BoundedPath($".{BacktestJobFiles.ResultManifestHash}.{nonce}.tmp");
        var manifestFinal = BoundedPath(BacktestJobFiles.ResultManifest);
        var hashFinal = BoundedPath(BacktestJobFiles.ResultManifestHash);
        var publishedHash = false;

        try
        {
            await WriteNewFileAsync(manifestTemp, manifestBytes, ct).ConfigureAwait(false);
            await WriteNewFileAsync(hashTemp, Encoding.ASCII.GetBytes(manifestHash + "\n"), ct).ConfigureAwait(false);

            // The hash is ready first. The manifest rename is the sole terminal publication boundary.
            File.Move(hashTemp, hashFinal, overwrite: false);
            publishedHash = true;
            File.Move(manifestTemp, manifestFinal, overwrite: false);
        }
        finally
        {
            TryDeleteFile(manifestTemp);
            TryDeleteFile(hashTemp);
            if (publishedHash && !File.Exists(manifestFinal)) TryDeleteFile(hashFinal);
        }
    }

    private string CreateStagingDirectory()
    {
        var path = BoundedPath($".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private string BoundedPath(string relativeName)
    {
        if (Path.IsPathFullyQualified(relativeName))
            throw new InvalidDataException("Worker-owned output paths must be relative.");

        var full = Path.GetFullPath(Path.Combine(_jobDirectory, relativeName));
        var prefix = _jobDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A worker-owned output path escaped the job directory.");
        return full;
    }

    private void EnsureTerminalManifestAbsent()
    {
        if (File.Exists(BoundedPath(BacktestJobFiles.ResultManifest)))
            throw new IOException("This job already has a terminal result manifest.");
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

    private async Task WriteJsonFileAsync<T>(string path, T value, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
        await using var bounded = new BoundedWriteStream(stream, request.Limits.MaxArtifactBytes);
        await JsonSerializer.SerializeAsync(bounded, value, BacktestProtocolJson.Options, ct).ConfigureAwait(false);
        await bounded.FlushAsync(ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            // The owning method flushes and disposes the underlying FileStream.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Reserve(int count)
        {
            if (count < 0 || _written > maxBytes - count)
                throw new InvalidDataException($"The report artifact exceeded its {maxBytes}-byte limit.");
            _written += count;
        }
    }

    private static void TryDeleteOwnedStaging(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { /* Best-effort cleanup of this invocation's unique staging directory. */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Best effort; terminal publication still hinges on the final manifest. */ }
    }
}
