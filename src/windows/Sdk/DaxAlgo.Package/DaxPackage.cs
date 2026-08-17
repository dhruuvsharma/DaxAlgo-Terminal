using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using DaxAlgo.Strategy.Bundle;

namespace DaxAlgo.Package;

/// <summary>
/// Reads and writes DaxAlgo submission packages — the single file a user sends us.
///
/// <para>Two extensions, one container. <c>.daxalgostrategy</c> carries a strategy,
/// <c>.daxalgovisualizer</c> carries a visualizer; the shape is identical and
/// <see cref="DaxPackageManifest.Kind"/> is what actually distinguishes them. The kind is recorded
/// INSIDE the manifest as well as in the file name, and <see cref="Read"/> cross-checks the two, so
/// renaming a file cannot change what it claims to be.</para>
///
/// <para><b>It carries everything.</b> Source, UI markup, compiled assemblies, dependencies,
/// resources, SBOM, provenance — whatever the author needs to hand over, all in one file, so a
/// marketplace submission is one artifact rather than a directory tree. Every payload is digested and
/// the manifest lists every digest, so nothing can be added, removed or altered after the fact
/// without the read failing.</para>
///
/// <para><b>Open and free</b> (MIT), deliberately: anyone must be able to write, read and inspect one
/// of these without our tooling. Sealing a reviewed submission into the closed <c>.daxq</c> format is
/// a separate, later step that happens on our systems.</para>
///
/// <para>This is a passive format. Reading a package never loads or executes a payload — it returns
/// bytes. Deciding whether those bytes are safe is the security review's job, not the reader's.</para>
/// </summary>
public static class DaxPackage
{
    /// <summary>Extension for a strategy submission.</summary>
    public const string StrategyExtension = ".daxalgostrategy";

    /// <summary>Extension for a visualizer submission.</summary>
    public const string VisualizerExtension = ".daxalgovisualizer";

    /// <summary>Where the manifest lives inside the archive.</summary>
    public const string ManifestEntryPath = "package.manifest.json";

    /// <summary>The extension a package of this kind must use.</summary>
    public static string ExtensionFor(DaxPackageKind kind) => kind switch
    {
        DaxPackageKind.Strategy => StrategyExtension,
        DaxPackageKind.Visualizer => VisualizerExtension,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind implied by a file name, or null when the extension is not one of ours.</summary>
    public static DaxPackageKind? KindFor(string path) =>
        path.EndsWith(StrategyExtension, StringComparison.OrdinalIgnoreCase) ? DaxPackageKind.Strategy
        : path.EndsWith(VisualizerExtension, StringComparison.OrdinalIgnoreCase) ? DaxPackageKind.Visualizer
        : null;

    /// <summary>
    /// The ONLY file types any edition installs. Defined here, once, so the open-source and installer
    /// editions cannot drift apart on what they accept.
    /// </summary>
    public static IReadOnlyList<string> AcceptedExtensions { get; } = [StrategyExtension, VisualizerExtension];

    /// <summary>An OpenFileDialog filter for exactly the accepted set.</summary>
    public static string OpenFileFilter =>
        $"DaxAlgo artifact (*{StrategyExtension};*{VisualizerExtension})|*{StrategyExtension};*{VisualizerExtension}";

    /// <summary>
    /// Whether a file may be offered for installation at all, before anything reads it.
    ///
    /// <para><c>.dll</c> and the legacy <c>.daxplugin</c> are refused by name rather than merely being
    /// absent from the accepted set, so the user is told why instead of watching the file silently fail
    /// to appear. Loading a raw assembly was removed on 2026-08-15; an artifact now reaches the app
    /// only as one of these two packages.</para>
    /// </summary>
    public static bool IsAccepted(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "No file was given.";
            return false;
        }

        if (KindFor(path) is not null)
        {
            reason = string.Empty;
            return true;
        }

        var extension = Path.GetExtension(path);
        reason = extension.ToLowerInvariant() switch
        {
            ".dll" =>
                "Raw assemblies are no longer installed. Repackage the strategy as a "
                + $"{StrategyExtension} (or {VisualizerExtension}) artifact.",
            ".daxplugin" =>
                $"The .daxplugin format was retired. Repackage as {StrategyExtension} or {VisualizerExtension}.",
            _ =>
                $"'{extension}' is not a DaxAlgo artifact. Only {StrategyExtension} and "
                + $"{VisualizerExtension} are installed.",
        };
        return false;
    }

    // ── Write ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Writes a package to disk. The extension must match <paramref name="request"/>'s kind.</summary>
    public static DaxPackageWriteResult Write(string outputPath, DaxPackageRequest request,
                                              DaxPackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);

        var expected = ExtensionFor(request.Kind);
        if (!outputPath.EndsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new DaxPackageException(DaxPackageError.KindMismatch,
                $"A {request.Kind} package must be written as '{expected}', not '{Path.GetExtension(outputPath)}'.");

        using var buffer = new MemoryStream();
        var result = Write(buffer, request, limits);
        var bytes = buffer.ToArray();
        File.WriteAllBytes(outputPath, bytes);
        return result with { Path = outputPath, Length = bytes.LongLength };
    }

    /// <summary>Writes a package to a stream.</summary>
    public static DaxPackageWriteResult Write(Stream output, DaxPackageRequest request,
                                              DaxPackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(request);
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));
        var bounds = limits ?? DaxPackageLimits.Default;

        Require(!string.IsNullOrWhiteSpace(request.Id), DaxPackageError.ManifestMalformed, "Id is required.");
        Require(!string.IsNullOrWhiteSpace(request.Version), DaxPackageError.ManifestMalformed, "Version is required.");
        Require(!string.IsNullOrWhiteSpace(request.DisplayName), DaxPackageError.ManifestMalformed, "DisplayName is required.");
        Require(!string.IsNullOrWhiteSpace(request.EntryTypeName), DaxPackageError.ManifestMalformed, "EntryTypeName is required.");

        var payloads = request.Payloads ?? [];
        Require(payloads.Count <= bounds.MaximumPayloadCount, DaxPackageError.LimitExceeded,
            $"A package may carry at most {bounds.MaximumPayloadCount} payloads.");

        var bytesByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var descriptors = new List<DaxPayloadDescriptor>();
        long total = 0;

        foreach (var source in payloads)
        {
            if (source is null) throw new DaxPackageException(DaxPackageError.ManifestMalformed,
                "Payload sources must not contain nulls.");

            var path = NormalizePath(source.Path, bounds);
            if (bytesByPath.ContainsKey(path))
                throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"Duplicate payload path '{path}'.");
            if (string.Equals(path, ManifestEntryPath, StringComparison.Ordinal))
                throw new DaxPackageException(DaxPackageError.ManifestMalformed,
                    $"'{ManifestEntryPath}' is reserved for the manifest.");

            using var stream = source.Open() ?? throw new DaxPackageException(
                DaxPackageError.PayloadMissing, $"Payload '{path}' opened as null.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var data = copy.ToArray();

            Require(data.LongLength <= bounds.MaximumPayloadBytes, DaxPackageError.LimitExceeded,
                $"Payload '{path}' is larger than the {bounds.MaximumPayloadBytes}-byte limit.");
            total += data.LongLength;
            Require(total <= bounds.MaximumTotalBytes, DaxPackageError.LimitExceeded,
                $"The package exceeds the {bounds.MaximumTotalBytes}-byte total limit.");

            bytesByPath[path] = data;
            descriptors.Add(new DaxPayloadDescriptor(path, source.Role, data.LongLength, Digest(data)));
        }

        // Ordered so the manifest bytes are reproducible: the same inputs must always produce the
        // same digest, whatever order the caller happened to supply payloads in.
        descriptors.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        var manifest = new DaxPackageManifest(
            DaxPackageManifest.CurrentFormat, DaxPackageManifest.CurrentFormatVersion,
            request.Kind, request.Id.Trim(), request.Version.Trim(), request.DisplayName.Trim(),
            request.Description, request.Publisher, request.EntryTypeName.Trim(), descriptors);

        var manifestBytes = DaxPackageManifestCodec.Write(manifest);

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ManifestEntryPath, manifestBytes);
            foreach (var d in descriptors)
                WriteEntry(archive, d.Path, bytesByPath[d.Path]);
        }

        return new DaxPackageWriteResult(string.Empty, manifest, Digest(manifestBytes), output.CanSeek ? output.Length : 0);
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads and fully verifies a package: the manifest parses, the file-name kind agrees with the
    /// manifest kind, every declared payload is present with a matching digest, and no undeclared
    /// entry is hiding in the archive.
    /// </summary>
    public static DaxPackageContents Read(string packagePath, DaxPackageLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        using var file = File.OpenRead(packagePath);
        var contents = Read(file, limits);

        var fromName = KindFor(packagePath);
        if (fromName is { } named && named != contents.Manifest.Kind)
            throw new DaxPackageException(DaxPackageError.KindMismatch,
                $"'{Path.GetFileName(packagePath)}' is named as a {named} package but its manifest declares {contents.Manifest.Kind}.");

        return contents;
    }

    /// <summary>Reads and verifies a package from a stream. The file-name check cannot apply here.</summary>
    public static DaxPackageContents Read(Stream input, DaxPackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bounds = limits ?? DaxPackageLimits.Default;

        ZipArchive archive;
        try { archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException ex)
        {
            throw new DaxPackageException(DaxPackageError.NotAnArchive, $"Not a readable package: {ex.Message}");
        }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(ManifestEntryPath)
                ?? throw new DaxPackageException(DaxPackageError.ManifestMissing,
                    $"The package has no '{ManifestEntryPath}'.");

            var manifest = DaxPackageManifestCodec.Read(ReadEntry(manifestEntry, bounds.MaximumPayloadBytes));

            if (!string.Equals(manifest.Format, DaxPackageManifest.CurrentFormat, StringComparison.Ordinal))
                throw new DaxPackageException(DaxPackageError.UnsupportedFormat,
                    $"Unknown package format '{manifest.Format}'.");
            if (manifest.FormatVersion > DaxPackageManifest.CurrentFormatVersion)
                throw new DaxPackageException(DaxPackageError.UnsupportedFormat,
                    $"Package format v{manifest.FormatVersion} is newer than this build understands.");

            var declared = new HashSet<string>(manifest.Payloads.Select(p => p.Path), StringComparer.Ordinal);
            var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long total = 0;

            foreach (var descriptor in manifest.Payloads)
            {
                // Normalise the DECLARED path too: a manifest is attacker-controlled, so a traversal
                // hidden in the manifest must be rejected exactly like one in a zip entry name.
                var path = NormalizePath(descriptor.Path, bounds);
                var entry = archive.GetEntry(path)
                    ?? throw new DaxPackageException(DaxPackageError.PayloadMissing,
                        $"Declared payload '{path}' is missing from the archive.");

                var data = ReadEntry(entry, bounds.MaximumPayloadBytes);
                total += data.LongLength;
                if (total > bounds.MaximumTotalBytes)
                    throw new DaxPackageException(DaxPackageError.LimitExceeded,
                        $"The package expands past the {bounds.MaximumTotalBytes}-byte total limit.");

                if (data.LongLength != descriptor.Length)
                    throw new DaxPackageException(DaxPackageError.PayloadDigestMismatch,
                        $"Payload '{path}' is {data.LongLength} bytes; the manifest declares {descriptor.Length}.");
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(Digest(data)), Encoding.ASCII.GetBytes(descriptor.Sha256)))
                    throw new DaxPackageException(DaxPackageError.PayloadDigestMismatch,
                        $"Payload '{path}' does not match its declared digest.");

                payloads[path] = data;
            }

            // An entry nobody declared is the interesting case: it ships inside the package, is not
            // covered by any digest, and a naive extractor would write it to disk.
            foreach (var entry in archive.Entries)
            {
                if (string.Equals(entry.FullName, ManifestEntryPath, StringComparison.Ordinal)) continue;
                if (entry.FullName.EndsWith('/')) continue;
                if (!declared.Contains(entry.FullName))
                    throw new DaxPackageException(DaxPackageError.PayloadUndeclared,
                        $"'{entry.FullName}' is in the archive but not declared in the manifest.");
            }

            return new DaxPackageContents(manifest, payloads);
        }
    }

    /// <summary>Reads only the manifest — for listing a submission without expanding its payloads.</summary>
    public static DaxPackageManifest Inspect(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        using var file = File.OpenRead(packagePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.GetEntry(ManifestEntryPath)
            ?? throw new DaxPackageException(DaxPackageError.ManifestMissing,
                $"The package has no '{ManifestEntryPath}'.");
        return DaxPackageManifestCodec.Read(ReadEntry(entry, DaxPackageLimits.Default.MaximumPayloadBytes));
    }

    /// <summary>
    /// Verifies and expands a package onto disk. Every path is re-normalised and confirmed to stay
    /// under <paramref name="destinationDirectory"/> before a single byte is written.
    /// </summary>
    public static DaxPackageManifest Extract(string packagePath, string destinationDirectory,
                                             DaxPackageLimits? limits = null)
    {
        var contents = Read(packagePath, limits);
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);

        foreach (var (relative, data) in contents.Payloads)
        {
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !string.Equals(target, root, StringComparison.Ordinal))
                throw new DaxPackageException(DaxPackageError.UnsafePath,
                    $"Payload '{relative}' would be written outside the destination.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }

        return contents.Manifest;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a payload path and places it under <c>payload/</c>.
    ///
    /// <para>The prefix is not decoration: it is the layout the shared normaliser enforces, and
    /// adopting it means this format reuses that function unchanged rather than forking a second
    /// implementation of "what does a zip-slip look like". It also keeps payloads in their own
    /// namespace, so no payload can ever collide with the manifest.</para>
    ///
    /// <para>Callers may pass either form — <c>src/Strategy.cs</c> or <c>payload/src/Strategy.cs</c> —
    /// and the manifest records the prefixed path either way.</para>
    /// </summary>
    private static string NormalizePath(string path, DaxPackageLimits bounds)
    {
        var candidate = (path ?? string.Empty).Replace('\\', '/');
        // Reject an absolute path rather than quietly containing it. Trimming the leading slash would
        // "work", but it would silently store something other than what the author asked for, and a
        // package that does not mean what it says is exactly what this format exists to prevent.
        if (candidate.StartsWith('/'))
            throw new DaxPackageException(DaxPackageError.UnsafePath, $"Payload path '{path}' is absolute.");

        if (!candidate.StartsWith("payload/", StringComparison.Ordinal))
            candidate = "payload/" + candidate;

        try
        {
            var normalized = StrategyBundlePath.NormalizePayloadPath(
                candidate, new StrategyBundleLimitOptions { MaxPathLength = bounds.MaximumPathLength },
                requireCanonical: false);
            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or StrategyBundleValidationException)
        {
            throw new DaxPackageException(DaxPackageError.UnsafePath, $"Unsafe payload path '{path}': {ex.Message}");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, long maximumBytes)
    {
        // Bounded copy: entry.Length is the archive's own claim and a hostile package can lie about it.
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long read = 0;
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            read += n;
            if (read > maximumBytes)
                throw new DaxPackageException(DaxPackageError.LimitExceeded,
                    $"'{entry.FullName}' expands past the {maximumBytes}-byte payload limit.");
            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] data)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    private static string Digest(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void Require(bool condition, DaxPackageError error, string message)
    {
        if (!condition) throw new DaxPackageException(error, message);
    }
}
