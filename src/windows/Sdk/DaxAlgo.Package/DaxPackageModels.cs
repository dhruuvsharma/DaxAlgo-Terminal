namespace DaxAlgo.Package;

/// <summary>
/// What an artifact is. This is the ONLY difference between the two package extensions, and it is
/// recorded inside the manifest as well as in the file name — a renamed file must not be able to
/// change what the package claims to be.
/// </summary>
public enum DaxPackageKind
{
    /// <summary>Publishes trading signals. Ships as <c>.daxalgostrategy</c>.</summary>
    Strategy,

    /// <summary>Draws; publishes no signals. Ships as <c>.daxalgovisualizer</c>.</summary>
    Visualizer,
}

/// <summary>
/// What a payload is for. The role drives review and installation, so it is declared rather than
/// guessed from the file extension.
/// </summary>
public enum DaxPayloadRole
{
    /// <summary>C# source. Present so a submission can be read by a human and re-reviewed later.</summary>
    Source,

    /// <summary>XAML or other UI markup.</summary>
    Ui,

    /// <summary>A compiled assembly built from this package's own source.</summary>
    Assembly,

    /// <summary>A third-party managed assembly the artifact depends on.</summary>
    Dependency,

    /// <summary>Images, data files, anything else the artifact reads at runtime.</summary>
    Resource,

    /// <summary>Software bill of materials.</summary>
    Sbom,

    /// <summary>Build provenance / attestation.</summary>
    Provenance,
}

/// <summary>One file inside the package, with the digest that pins it.</summary>
public sealed record DaxPayloadDescriptor(
    string Path,
    DaxPayloadRole Role,
    long Length,
    string Sha256);

/// <summary>A repeatable source for one payload. The writer opens and disposes each stream.</summary>
public sealed class DaxPayloadSource(string path, DaxPayloadRole role, Func<Stream> open)
{
    public string Path { get; } = path;
    public DaxPayloadRole Role { get; } = role;
    public Func<Stream> Open { get; } = open;

    public static DaxPayloadSource FromFile(string packagePath, DaxPayloadRole role, string filePath) =>
        new(packagePath, role, () => File.OpenRead(filePath));

    public static DaxPayloadSource FromBytes(string packagePath, DaxPayloadRole role, byte[] bytes) =>
        new(packagePath, role, () => new MemoryStream(bytes, writable: false));
}

/// <summary>
/// The package manifest. This is a WIRE CONTRACT: it is serialised as canonical JSON and digested, so
/// fields may be added but never renamed or reordered in a way that changes meaning.
/// </summary>
public sealed record DaxPackageManifest(
    string Format,
    int FormatVersion,
    DaxPackageKind Kind,
    string Id,
    string Version,
    string DisplayName,
    string? Description,
    string? Publisher,
    string EntryTypeName,
    IReadOnlyList<DaxPayloadDescriptor> Payloads)
{
    public const string CurrentFormat = "daxpackage";
    public const int CurrentFormatVersion = 1;
}

/// <summary>What to put in a package.</summary>
public sealed record DaxPackageRequest
{
    public required DaxPackageKind Kind { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The type the host instantiates. Resolved exactly; never scanned for or guessed.</summary>
    public required string EntryTypeName { get; init; }

    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public IReadOnlyList<DaxPayloadSource> Payloads { get; init; } = [];
}

/// <summary>The outcome of writing a package.</summary>
public sealed record DaxPackageWriteResult(
    string Path,
    DaxPackageManifest Manifest,
    string ManifestSha256,
    long Length);

/// <summary>A package read back: its manifest plus every payload's bytes.</summary>
public sealed record DaxPackageContents(
    DaxPackageManifest Manifest,
    IReadOnlyDictionary<string, byte[]> Payloads);

/// <summary>Why a package was rejected.</summary>
public enum DaxPackageError
{
    NotAnArchive,
    ManifestMissing,
    ManifestMalformed,
    UnsupportedFormat,
    KindMismatch,
    PayloadMissing,
    PayloadDigestMismatch,
    PayloadUndeclared,
    LimitExceeded,
    UnsafePath,
}

public sealed class DaxPackageException(DaxPackageError error, string message)
    : Exception(message)
{
    public DaxPackageError Error { get; } = error;
}

/// <summary>Bounds applied while reading, so a hostile package cannot exhaust memory or disk.</summary>
public sealed record DaxPackageLimits
{
    public static DaxPackageLimits Default { get; } = new();

    /// <summary>Largest single payload, expanded.</summary>
    public long MaximumPayloadBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Largest total expanded size across all payloads — the zip-bomb bound.</summary>
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;

    public int MaximumPayloadCount { get; init; } = 4096;

    public int MaximumPathLength { get; init; } = 200;

    /// <summary>How deep a payload path may nest below <c>payload/</c>. A bound on nesting, not just
    /// on length — deep trees are how an extractor gets walked past its own root.</summary>
    public int MaximumPathDepth { get; init; } = 8;
}
