using System.Security.Cryptography;

namespace DaxAlgo.Strategy.Bundle;

public enum StrategyBundlePayloadRole
{
    Engine,
    WindowsUi,
    ManagedDependency,
    Resource,
    Sbom,
    Provenance,
}

public sealed record StrategyBundleIdentity(
    string Id,
    string Name,
    string Version,
    string PublisherId);

public sealed record StrategyBundleCompatibility(
    string TargetSdkVersion,
    string? MinimumHostVersion = null,
    string? MaximumHostVersion = null);

/// <summary>
/// Identifies the one factory that creates the canonical headless strategy engine. A consumer resolves
/// <see cref="AssemblyPath"/> inside the verified bundle, then resolves <see cref="TypeName"/> exactly;
/// it never scans assemblies or guesses a constructor.
/// </summary>
public sealed record StrategyBundleEngineEntryPoint(
    string AssemblyPath,
    string TypeName,
    string Contract,
    string Activation)
{
    public const string CurrentContract = "daxalgo.strategy-engine-factory/1";
    public const string CurrentActivation = "public-parameterless-constructor";
}

public sealed record StrategyBundlePayloadDescriptor(
    string Path,
    StrategyBundlePayloadRole Role,
    long Length,
    string Sha256);

/// <summary>
/// Metadata-derived managed assembly graph. Names and references are informational for inspection and
/// provide an exact path map for a future loader; verification recomputes them from PE metadata.
/// </summary>
public sealed record StrategyBundleManagedAssemblyDescriptor(
    string Path,
    string Name,
    IReadOnlyList<string> References);

/// <summary>
/// One verified manifest entry in the headless engine's managed assembly closure. The engine is
/// always the first resolved entry; reachable private dependencies follow in ordinal path order.
/// </summary>
public sealed record StrategyBundleEngineAssemblyDescriptor(
    string Path,
    StrategyBundlePayloadRole Role,
    string Name,
    IReadOnlyList<string> References,
    long Length,
    string Sha256);

public sealed record StrategyBundleManifest(
    string Format,
    int FormatVersion,
    StrategyBundleIdentity Identity,
    StrategyBundleCompatibility Compatibility,
    StrategyBundleEngineEntryPoint Engine,
    IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> ManagedAssemblies,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<StrategyBundlePayloadDescriptor> Payloads)
{
    public const string CurrentFormat = "daxstrategy";
    public const int CurrentFormatVersion = 1;
}

/// <summary>A repeatable source for one payload. The bundle packer owns and disposes each opened stream.</summary>
public sealed class StrategyBundlePayloadSource
{
    private readonly Func<Stream> _openRead;

    public StrategyBundlePayloadSource(
        string path,
        StrategyBundlePayloadRole role,
        Func<Stream> openRead)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Role = role;
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
    }

    public string Path { get; }
    public StrategyBundlePayloadRole Role { get; }

    public static StrategyBundlePayloadSource FromFile(
        string bundlePath,
        StrategyBundlePayloadRole role,
        string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        return new StrategyBundlePayloadSource(
            bundlePath,
            role,
            () => new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    public static StrategyBundlePayloadSource FromBytes(
        string bundlePath,
        StrategyBundlePayloadRole role,
        ReadOnlySpan<byte> content)
    {
        var owned = content.ToArray();
        return new StrategyBundlePayloadSource(
            bundlePath,
            role,
            () => new MemoryStream(owned, writable: false));
    }

    internal Stream OpenRead()
    {
        var stream = _openRead()
            ?? throw new InvalidOperationException($"Payload source '{Path}' returned a null stream.");
        if (!stream.CanRead)
        {
            stream.Dispose();
            throw new InvalidOperationException($"Payload source '{Path}' returned an unreadable stream.");
        }

        return stream;
    }
}

public sealed record StrategyBundlePackRequest(
    StrategyBundleIdentity Identity,
    StrategyBundleCompatibility Compatibility,
    StrategyBundleEngineEntryPoint Engine,
    IReadOnlyCollection<string> Capabilities,
    IReadOnlyCollection<StrategyBundlePayloadSource> Payloads);

public sealed record StrategyBundlePublisherKey(
    string PublisherId,
    string KeyId,
    ReadOnlyMemory<byte> SubjectPublicKeyInfo)
{
    public static StrategyBundlePublisherKey FromEcdsa(string publisherId, string keyId, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new StrategyBundlePublisherKey(publisherId, keyId, key.ExportSubjectPublicKeyInfo());
    }
}

public enum StrategyBundleSignatureStatus
{
    Missing,
    PresentUnverified,
    Verified,
    UnknownKey,
    Invalid,
}

public sealed record StrategyBundleSignatureEvidence(
    StrategyBundleSignatureStatus Status,
    string? KeyId,
    string? PayloadType,
    string Algorithm,
    string? Detail = null)
{
    public const string PublisherAlgorithm = "ECDSA-P256-SHA256-IEEE-P1363";

    /// <summary>SHA-256 of the accepted publisher SubjectPublicKeyInfo bytes.</summary>
    public string? KeyFingerprintSha256 { get; init; }
}

public sealed record StrategyBundleInspection(
    StrategyBundleManifest Manifest,
    string ContentRootSha256,
    StrategyBundleSignatureEvidence PublisherSignature,
    long CompressedBundleLength,
    long TotalExpandedLength);

public sealed record StrategyBundleVerification(
    StrategyBundleInspection Inspection,
    StrategyBundleSignatureEvidence PublisherSignature)
{
    public bool IsPublisherVerified => PublisherSignature.Status == StrategyBundleSignatureStatus.Verified;
}

public sealed record StrategyBundlePackResult(
    StrategyBundleManifest Manifest,
    string ContentRootSha256,
    long BundleLength);

public sealed record StrategyBundleSignResult(
    StrategyBundleManifest Manifest,
    string ContentRootSha256,
    string KeyId,
    long BundleLength);

public enum StrategyBundleValidationError
{
    InvalidArchive,
    LimitExceeded,
    InvalidPath,
    DuplicatePath,
    InvalidManifest,
    UnsupportedFormat,
    UnsupportedVersion,
    InvalidPayloadSet,
    PayloadMismatch,
    InvalidSignatureEnvelope,
}

public sealed class StrategyBundleValidationException : Exception
{
    public StrategyBundleValidationException(StrategyBundleValidationError error, string message)
        : base(message) => Error = error;

    public StrategyBundleValidationException(
        StrategyBundleValidationError error,
        string message,
        Exception innerException)
        : base(message, innerException) => Error = error;

    public StrategyBundleValidationError Error { get; }
}
