using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>
/// Installs verified strategy bundles into immutable content and archive-evidence namespaces. The
/// mutable activation layer contains only atomic pointers and never executable bytes.
/// </summary>
public sealed class StrategyBundleStore
{
    private const string StoredManifestFileName = DaxStrategyBundle.ManifestEntryPath;
    private const string StoredArchiveFileName = "bundle.daxstrategy";
    private const string StoredReceiptFileName = "install.receipt.json";
    private const int MaximumReceiptBytes = 64 * 1024;
    private const int MaximumActivationBytes = 16 * 1024;

    private readonly StrategyBundleLimitOptions _limits;
    private readonly string _objectsDirectory;
    private readonly string _evidenceDirectory;
    private readonly string _activationsDirectory;
    private readonly string _stagingDirectory;

    public StrategyBundleStore(string rootDirectory, StrategyBundleLimitOptions? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _limits = (limits ?? StrategyBundleLimitOptions.Default).Checked();
        RootDirectory = Path.GetFullPath(rootDirectory);
        var filesystemRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(RootDirectory)
            ?? throw new ArgumentException("The strategy store path has no filesystem root.", nameof(rootDirectory)));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(RootDirectory),
                filesystemRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The strategy store cannot be a filesystem root.", nameof(rootDirectory));
        _objectsDirectory = Path.Combine(RootDirectory, "objects", "sha256");
        _evidenceDirectory = Path.Combine(RootDirectory, "evidence", "sha256");
        _activationsDirectory = Path.Combine(RootDirectory, "activations");
        _stagingDirectory = Path.Combine(RootDirectory, ".staging");

        EnsureNoReparseExistingComponents(RootDirectory);
        EnsurePlainDirectory(RootDirectory);
        EnsureNoReparseComponents(RootDirectory);
        EnsurePlainDirectory(Path.Combine(RootDirectory, "objects"));
        EnsurePlainDirectory(_objectsDirectory);
        EnsurePlainDirectory(Path.Combine(RootDirectory, "evidence"));
        EnsurePlainDirectory(_evidenceDirectory);
        EnsurePlainDirectory(_activationsDirectory);
        EnsurePlainDirectory(_stagingDirectory);
    }

    public string RootDirectory { get; }

    public StrategyBundleInstallation Install(
        string bundlePath,
        StrategyBundleInstallPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        using var input = new FileStream(
            bundlePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        return Install(input, policy);
    }

    public StrategyBundleInstallation Install(
        Stream bundle,
        StrategyBundleInstallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(policy);
        if (!bundle.CanRead) throw new ArgumentException("The bundle stream must be readable.", nameof(bundle));

        var archiveBytes = ReadBounded(bundle, _limits.MaxCompressedBundleBytes, "strategy bundle");
        var archiveSha256 = Hash(archiveBytes);
        var read = ReadArchive(archiveBytes);
        var verification = VerifyArchive(archiveBytes, policy);
        ValidatePolicy(read.Manifest, verification.PublisherSignature, policy);
        var receipt = CreateReceipt(read, archiveSha256, verification.PublisherSignature);

        var contentDirectory = ContentDirectory(read.ContentRootSha256);
        EnsureImmutableDirectory(
            contentDirectory,
            "content",
            staging => WriteContentObject(staging, read),
            () => VerifyContentObject(contentDirectory, read));

        var evidenceDirectory = EvidenceDirectory(archiveSha256);
        EnsureImmutableDirectory(
            evidenceDirectory,
            "evidence",
            staging => WriteEvidenceObject(staging, archiveBytes, receipt),
            () => VerifyEvidenceObject(evidenceDirectory, read, archiveBytes, receipt));

        return VerifyInstallation(read.ContentRootSha256, archiveSha256, policy);
    }

    public StrategyBundleInstallation VerifyInstallation(
        string contentRootSha256,
        string archiveSha256,
        StrategyBundleInstallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateSha256(contentRootSha256, nameof(contentRootSha256));
        ValidateSha256(archiveSha256, nameof(archiveSha256));

        var contentDirectory = ContentDirectory(contentRootSha256);
        var evidenceDirectory = EvidenceDirectory(archiveSha256);
        if (!PathExists(contentDirectory) || !PathExists(evidenceDirectory))
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.InstallationNotFound,
                $"Strategy installation '{contentRootSha256}' / '{archiveSha256}' does not exist.");
        }

        try
        {
            VerifyExactTree(
                evidenceDirectory,
                [StoredArchiveFileName, StoredReceiptFileName],
                []);
            var archivePath = Path.Combine(evidenceDirectory, StoredArchiveFileName);
            var archiveBytes = ReadRegularFile(archivePath, _limits.MaxCompressedBundleBytes, "stored strategy archive");
            if (!FixedHashEquals(Hash(archiveBytes), archiveSha256))
                Corrupt("The stored strategy archive does not match its evidence address.");

            var read = ReadArchive(archiveBytes);
            if (!FixedHashEquals(read.ContentRootSha256, contentRootSha256))
                Corrupt("The stored strategy archive does not match its content address.");

            var verification = VerifyArchive(archiveBytes, policy);
            ValidatePolicy(read.Manifest, verification.PublisherSignature, policy);
            var expectedReceipt = CreateReceipt(read, archiveSha256, verification.PublisherSignature);
            var receiptBytes = ReadRegularFile(
                Path.Combine(evidenceDirectory, StoredReceiptFileName),
                MaximumReceiptBytes,
                "install receipt");
            var receipt = StrategyBundleStoreJson.ParseReceipt(receiptBytes);
            if (receipt != expectedReceipt)
                Corrupt("The install receipt does not describe the stored archive and accepted publisher evidence.");

            VerifyContentObject(contentDirectory, read);
            return new StrategyBundleInstallation(
                receipt,
                read.Manifest,
                contentDirectory,
                Path.Combine(contentDirectory, StoredManifestFileName),
                evidenceDirectory,
                Path.Combine(evidenceDirectory, StoredReceiptFileName),
                archivePath);
        }
        catch (StrategyBundleStoreException)
        {
            throw;
        }
        catch (StrategyBundleValidationException ex)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.CorruptInstallation,
                "The installed strategy archive no longer passes bundle validation.",
                ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.CorruptInstallation,
                "The installed strategy could not be read safely.",
                ex);
        }
    }

    /// <summary>Atomically makes one already-installed evidence selection active.</summary>
    public StrategyBundleInstallation Activate(
        string contentRootSha256,
        string archiveSha256,
        StrategyBundleInstallPolicy policy)
    {
        var installation = VerifyInstallation(contentRootSha256, archiveSha256, policy);
        var strategyId = NormalizeStrategyId(installation.Manifest.Identity.Id, nameof(contentRootSha256));
        var activation = new StrategyBundleActivationPointer(
            StrategyBundleActivationPointer.CurrentSchema,
            StrategyBundleActivationPointer.CurrentSchemaVersion,
            strategyId,
            contentRootSha256,
            archiveSha256);
        PublishActivation(strategyId, StrategyBundleStoreJson.WriteActivation(activation));
        return installation;
    }

    /// <summary>Resolves an activation only after rechecking its exact files, compatibility, and trust.</summary>
    public StrategyBundleInstallation ResolveActive(
        string strategyId,
        StrategyBundleInstallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalizedId = NormalizeStrategyId(strategyId, nameof(strategyId));
        var activationPath = ActivationPath(normalizedId);
        if (!PathExists(activationPath))
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.InstallationNotFound,
                $"Strategy '{normalizedId}' has no active installation.");
        }

        var activationBytes = ReadRegularFile(
            activationPath,
            MaximumActivationBytes,
            "activation pointer",
            FileShare.Read | FileShare.Delete);
        var activation = StrategyBundleStoreJson.ParseActivation(activationBytes);
        if (!string.Equals(activation.StrategyId, normalizedId, StringComparison.Ordinal))
            Corrupt("The activation pointer strategy ID does not match its file name.");

        var installation = VerifyInstallation(
            activation.ContentRootSha256,
            activation.ArchiveSha256,
            policy);
        if (!string.Equals(installation.Manifest.Identity.Id, normalizedId, StringComparison.Ordinal))
            Corrupt("The active installation belongs to a different strategy ID.");
        return installation;
    }

    private StrategyBundleReadResult ReadArchive(byte[] archiveBytes)
    {
        using var input = new MemoryStream(archiveBytes, writable: false);
        return StrategyBundleArchive.Read(input, _limits);
    }

    private StrategyBundleVerification VerifyArchive(
        byte[] archiveBytes,
        StrategyBundleInstallPolicy policy)
    {
        using var input = new MemoryStream(archiveBytes, writable: false);
        return DaxStrategyBundle.Verify(input, policy.TrustedPublisherKeys, _limits);
    }

    private static StrategyBundleInstallReceipt CreateReceipt(
        StrategyBundleReadResult read,
        string archiveSha256,
        StrategyBundleSignatureEvidence evidence) => new(
        StrategyBundleInstallReceipt.CurrentSchema,
        StrategyBundleInstallReceipt.CurrentSchemaVersion,
        read.ContentRootSha256,
        archiveSha256,
        read.CompressedLength,
        read.Manifest.Identity,
        read.Manifest.Compatibility,
        evidence with { Detail = null });

    private static void ValidatePolicy(
        StrategyBundleManifest manifest,
        StrategyBundleSignatureEvidence evidence,
        StrategyBundleInstallPolicy policy)
    {
        switch (evidence.Status)
        {
            case StrategyBundleSignatureStatus.Verified:
                break;
            case StrategyBundleSignatureStatus.Missing
                when policy.TrustMode == StrategyBundleTrustMode.LocalDevelopment:
                break;
            case StrategyBundleSignatureStatus.Missing:
                throw new StrategyBundleStoreException(
                    StrategyBundleStoreError.SignatureRejected,
                    "A verified publisher signature is required by the current install policy.");
            case StrategyBundleSignatureStatus.Invalid:
                throw new StrategyBundleStoreException(
                    StrategyBundleStoreError.SignatureRejected,
                    "The publisher signature is invalid and cannot be installed under any policy.");
            case StrategyBundleSignatureStatus.UnknownKey:
            case StrategyBundleSignatureStatus.PresentUnverified:
            default:
                throw new StrategyBundleStoreException(
                    StrategyBundleStoreError.SignatureRejected,
                    "A present publisher signature must verify against a trusted publisher key.");
        }

        if (!string.Equals(
                manifest.Compatibility.TargetSdkVersion,
                policy.SdkVersion,
                StringComparison.Ordinal))
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.IncompatibleSdk,
                $"Strategy SDK target '{manifest.Compatibility.TargetSdkVersion}' is not compatible with host SDK '{policy.SdkVersion}'.");
        }

        var currentHost = StrategyBundleSemanticVersion.Parse(policy.HostVersion, nameof(policy.HostVersion));
        if (manifest.Compatibility.MinimumHostVersion is { } minimumHost &&
            currentHost.CompareTo(StrategyBundleSemanticVersion.Parse(minimumHost, nameof(minimumHost))) < 0)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.IncompatibleHost,
                $"Strategy requires host version '{minimumHost}' or later; current host is '{policy.HostVersion}'.");
        }
        if (manifest.Compatibility.MaximumHostVersion is { } maximumHost &&
            currentHost.CompareTo(StrategyBundleSemanticVersion.Parse(maximumHost, nameof(maximumHost))) > 0)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.IncompatibleHost,
                $"Strategy supports host version '{maximumHost}' or earlier; current host is '{policy.HostVersion}'.");
        }
    }

    private void WriteContentObject(string stagingDirectory, StrategyBundleReadResult read)
    {
        WriteNewFile(Path.Combine(stagingDirectory, StoredManifestFileName), read.ManifestBytes);
        foreach (var (path, content) in read.Payloads.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var destination = StorePath(stagingDirectory, path);
            EnsurePlainDirectory(Path.GetDirectoryName(destination)!);
            WriteNewFile(destination, content);
        }
    }

    private static void WriteEvidenceObject(
        string stagingDirectory,
        byte[] archiveBytes,
        StrategyBundleInstallReceipt receipt)
    {
        WriteNewFile(Path.Combine(stagingDirectory, StoredArchiveFileName), archiveBytes);
        WriteNewFile(
            Path.Combine(stagingDirectory, StoredReceiptFileName),
            StrategyBundleStoreJson.WriteReceipt(receipt));
    }

    private void VerifyEvidenceObject(
        string evidenceDirectory,
        StrategyBundleReadResult expectedRead,
        byte[] expectedArchiveBytes,
        StrategyBundleInstallReceipt expectedReceipt)
    {
        VerifyExactTree(evidenceDirectory, [StoredArchiveFileName, StoredReceiptFileName], []);
        var archiveBytes = ReadRegularFile(
            Path.Combine(evidenceDirectory, StoredArchiveFileName),
            _limits.MaxCompressedBundleBytes,
            "stored strategy archive");
        if (!archiveBytes.AsSpan().SequenceEqual(expectedArchiveBytes))
            Corrupt("The existing archive evidence does not match its immutable address.");

        var receiptBytes = ReadRegularFile(
            Path.Combine(evidenceDirectory, StoredReceiptFileName),
            MaximumReceiptBytes,
            "install receipt");
        if (StrategyBundleStoreJson.ParseReceipt(receiptBytes) != expectedReceipt)
            Corrupt("The existing install receipt does not match its immutable archive evidence.");

        var read = ReadArchive(archiveBytes);
        if (!FixedHashEquals(read.ContentRootSha256, expectedRead.ContentRootSha256))
            Corrupt("The existing archive evidence resolves to a different content object.");
    }

    private void VerifyContentObject(string contentDirectory, StrategyBundleReadResult read)
    {
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal) { StoredManifestFileName };
        foreach (var payload in read.Manifest.Payloads) expectedFiles.Add(payload.Path);
        var expectedDirectories = RequiredDirectories(expectedFiles);
        VerifyExactTree(contentDirectory, expectedFiles, expectedDirectories);

        var manifestBytes = ReadRegularFile(
            Path.Combine(contentDirectory, StoredManifestFileName),
            _limits.MaxManifestBytes,
            "stored bundle manifest");
        if (!manifestBytes.AsSpan().SequenceEqual(read.ManifestBytes))
            Corrupt("The stored canonical manifest does not match its content root.");

        long expandedTotal = manifestBytes.LongLength;
        foreach (var descriptor in read.Manifest.Payloads)
        {
            var payload = ReadRegularFile(
                StorePath(contentDirectory, descriptor.Path),
                _limits.MaxEntryExpandedBytes,
                $"stored payload '{descriptor.Path}'");
            expandedTotal = checked(expandedTotal + payload.LongLength);
            if (expandedTotal > _limits.MaxTotalExpandedBytes)
                Corrupt("The stored content object exceeds the configured expanded-size limit.");
            if (payload.LongLength != descriptor.Length ||
                !FixedHashEquals(Hash(payload), descriptor.Sha256) ||
                !payload.AsSpan().SequenceEqual(read.Payloads[descriptor.Path]))
            {
                Corrupt($"Stored payload '{descriptor.Path}' does not match the verified archive.");
            }
        }
    }

    private void EnsureImmutableDirectory(
        string targetDirectory,
        string kind,
        Action<string> write,
        Action verify)
    {
        if (PathExists(targetDirectory))
        {
            verify();
            return;
        }

        var stagingDirectory = CreateStagingDirectory(kind);
        try
        {
            write(stagingDirectory);
            try
            {
                Directory.Move(stagingDirectory, targetDirectory);
            }
            catch (IOException) when (PathExists(targetDirectory))
            {
                TryDeleteOwnedStagingDirectory(stagingDirectory);
            }
            verify();
        }
        finally
        {
            TryDeleteOwnedStagingDirectory(stagingDirectory);
        }
    }

    private string CreateStagingDirectory(string kind)
    {
        EnsurePlainDirectory(_stagingDirectory);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(_stagingDirectory, $"{kind}-{Guid.NewGuid():N}");
            if (PathExists(path)) continue;
            Directory.CreateDirectory(path);
            EnsurePlainDirectory(path);
            return path;
        }
        throw new IOException("Could not allocate a unique same-volume strategy-store staging directory.");
    }

    private void PublishActivation(string strategyId, byte[] content)
    {
        EnsurePlainDirectory(_activationsDirectory);
        var destination = ActivationPath(strategyId);
        if (PathExists(destination)) EnsureRegularFile(destination, "activation pointer");
        var temporary = Path.Combine(_activationsDirectory, $".{strategyId}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNewFile(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void VerifyExactTree(
        string root,
        IReadOnlyCollection<string> expectedFiles,
        IReadOnlyCollection<string> expectedDirectories)
    {
        EnsurePlainDirectory(root, create: false);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    Corrupt($"Stored path '{Path.GetRelativePath(root, entry)}' is a reparse point or device.");

                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!directories.Add(relative)) Corrupt($"Stored directory '{relative}' is duplicated.");
                    pending.Push(entry);
                }
                else
                {
                    if (!files.Add(relative)) Corrupt($"Stored file '{relative}' is duplicated.");
                }
            }
        }

        if (!files.SetEquals(expectedFiles) || !directories.SetEquals(expectedDirectories))
            Corrupt("The immutable installation contains missing, extra, or case-aliased files or directories.");
    }

    private static IReadOnlyCollection<string> RequiredDirectories(IEnumerable<string> files)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.Split('/');
            for (var count = 1; count < segments.Length; count++)
                directories.Add(string.Join('/', segments.Take(count)));
        }
        return directories;
    }

    private static byte[] ReadRegularFile(
        string path,
        long maximumBytes,
        string label,
        FileShare share = FileShare.Read)
    {
        EnsureRegularFile(path, label);
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            share,
            81920,
            FileOptions.SequentialScan);
        if (input.Length < 0 || input.Length > maximumBytes)
            Corrupt($"The {label} exceeds its configured size limit.");
        var bytes = ReadBounded(input, maximumBytes, label);
        EnsureRegularFile(path, label);
        return bytes;
    }

    private static byte[] ReadBounded(Stream input, long maximumBytes, string label)
    {
        if (maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            if (remaining < 0 || remaining > maximumBytes)
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.LimitExceeded,
                    $"The {label} exceeds its configured size limit.");
        }

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.LimitExceeded,
                    $"The {label} exceeds its configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> content)
    {
        using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough);
        output.Write(content);
        output.Flush(flushToDisk: true);
    }

    private static void EnsurePlainDirectory(string path, bool create = true)
    {
        if (create) Directory.CreateDirectory(path);
        if (!Directory.Exists(path)) Corrupt($"Required store directory '{path}' is missing.");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            Corrupt($"Store directory '{path}' is not a plain directory.");
        }
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new ArgumentException("The strategy store path has no filesystem root.", nameof(path));
        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                Corrupt($"Strategy store path component '{current}' is a reparse point or device.");
        }
    }

    private static void EnsureNoReparseExistingComponents(string path)
    {
        var current = Path.GetFullPath(path);
        while (!PathExists(current))
        {
            current = Path.GetDirectoryName(current)
                      ?? throw new ArgumentException(
                          "The strategy store path has no existing filesystem ancestor.",
                          nameof(path));
        }
        EnsureNoReparseComponents(current);
    }

    private static void EnsureRegularFile(string path, string label)
    {
        if (!File.Exists(path)) Corrupt($"The {label} is missing.");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            Corrupt($"The {label} is not a regular file.");
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void TryDeleteOwnedStagingDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return;
            DeletePlainDirectoryTree(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A crash-safe store may retain an unreferenced staging directory; it is never executable.
        }
    }

    private static void DeletePlainDirectoryTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return;
            if ((attributes & FileAttributes.Directory) != 0) DeletePlainDirectoryTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    private string ContentDirectory(string contentRootSha256) =>
        Path.Combine(_objectsDirectory, contentRootSha256);

    private string EvidenceDirectory(string archiveSha256) =>
        Path.Combine(_evidenceDirectory, archiveSha256);

    private string ActivationPath(string strategyId) =>
        Path.Combine(_activationsDirectory, $"{strategyId}.json");

    private static string StorePath(string root, string portablePath) =>
        Path.Combine(root, portablePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeStrategyId(string? strategyId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId, parameterName);
        if (strategyId.Length > 128 ||
            !char.IsAsciiLetterOrDigit(strategyId[0]) ||
            strategyId.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')) ||
            !string.Equals(strategyId, strategyId.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The strategy ID must be a lowercase portable identifier.", parameterName);
        }
        return strategyId;
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(static character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The value must be a lowercase SHA-256 digest.", parameterName);
        }
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool FixedHashEquals(string actual, string expected) =>
        actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual),
            Encoding.ASCII.GetBytes(expected));

    [DoesNotReturn]
    private static void Corrupt(string message) =>
        throw new StrategyBundleStoreException(StrategyBundleStoreError.CorruptInstallation, message);
}
