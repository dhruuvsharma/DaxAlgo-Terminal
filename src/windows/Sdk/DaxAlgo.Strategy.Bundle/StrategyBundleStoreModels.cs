namespace DaxAlgo.Strategy.Bundle;

/// <summary>Controls whether an installed strategy may be unsigned.</summary>
public enum StrategyBundleTrustMode
{
    /// <summary>Allows an unsigned local bundle. A present signature must still verify.</summary>
    LocalDevelopment,

    /// <summary>Requires a publisher signature that verifies against the supplied trust roots.</summary>
    RequireVerifiedPublisher,
}

/// <summary>Current host compatibility and publisher trust used for install, activation, and resolve.</summary>
public sealed class StrategyBundleInstallPolicy
{
    private readonly IReadOnlyList<StrategyBundlePublisherKey> _trustedPublisherKeys;

    public StrategyBundleInstallPolicy(
        string hostVersion,
        string sdkVersion,
        StrategyBundleTrustMode trustMode,
        IEnumerable<StrategyBundlePublisherKey>? trustedPublisherKeys = null)
    {
        HostVersion = StrategyBundleSemanticVersion.Normalize(hostVersion, nameof(hostVersion));
        SdkVersion = StrategyBundleSemanticVersion.Normalize(sdkVersion, nameof(sdkVersion));
        if (!Enum.IsDefined(trustMode)) throw new ArgumentOutOfRangeException(nameof(trustMode));
        TrustMode = trustMode;

        var keys = new List<StrategyBundlePublisherKey>();
        foreach (var key in trustedPublisherKeys ?? [])
        {
            if (key is null)
                throw new ArgumentException("Trusted publisher keys must not contain null entries.", nameof(trustedPublisherKeys));
            keys.Add(key with { SubjectPublicKeyInfo = key.SubjectPublicKeyInfo.ToArray() });
        }
        _trustedPublisherKeys = keys.AsReadOnly();
    }

    public string HostVersion { get; }
    public string SdkVersion { get; }
    public StrategyBundleTrustMode TrustMode { get; }
    public IReadOnlyList<StrategyBundlePublisherKey> TrustedPublisherKeys => _trustedPublisherKeys
        .Select(static key => key with { SubjectPublicKeyInfo = key.SubjectPublicKeyInfo.ToArray() })
        .ToArray();

    public static StrategyBundleInstallPolicy LocalDevelopment(
        string hostVersion,
        string sdkVersion,
        IEnumerable<StrategyBundlePublisherKey>? trustedPublisherKeys = null) =>
        new(hostVersion, sdkVersion, StrategyBundleTrustMode.LocalDevelopment, trustedPublisherKeys);

    public static StrategyBundleInstallPolicy VerifiedPublisher(
        string hostVersion,
        string sdkVersion,
        IEnumerable<StrategyBundlePublisherKey> trustedPublisherKeys) =>
        new(hostVersion, sdkVersion, StrategyBundleTrustMode.RequireVerifiedPublisher, trustedPublisherKeys);
}

/// <summary>
/// Immutable evidence metadata stored beside one exact outer archive. It intentionally does not
/// contain an install timestamp, so concurrent installs of the same archive produce identical bytes.
/// </summary>
public sealed record StrategyBundleInstallReceipt(
    string Schema,
    int SchemaVersion,
    string ContentRootSha256,
    string ArchiveSha256,
    long ArchiveLength,
    StrategyBundleIdentity Identity,
    StrategyBundleCompatibility Compatibility,
    StrategyBundleSignatureEvidence PublisherSignature)
{
    public const string CurrentSchema = "daxalgo.strategy-install-receipt";
    public const int CurrentSchemaVersion = 1;
}

/// <summary>A fully re-verified immutable store selection.</summary>
public sealed record StrategyBundleInstallation(
    StrategyBundleInstallReceipt Receipt,
    StrategyBundleManifest Manifest,
    string ObjectDirectory,
    string ManifestPath,
    string EvidenceDirectory,
    string ReceiptPath,
    string ArchivePath);

public enum StrategyBundleStoreError
{
    IncompatibleSdk,
    IncompatibleHost,
    SignatureRejected,
    InstallationNotFound,
    CorruptInstallation,
}

public sealed class StrategyBundleStoreException : Exception
{
    public StrategyBundleStoreException(StrategyBundleStoreError error, string message)
        : base(message) => Error = error;

    public StrategyBundleStoreException(StrategyBundleStoreError error, string message, Exception innerException)
        : base(message, innerException) => Error = error;

    public StrategyBundleStoreError Error { get; }
}
