using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaxAlgo.Strategy.Bundle;

internal sealed record StrategyBundleEnvelope(string KeyId, byte[] Signature, byte[] CanonicalBytes);

internal sealed record StrategyBundleReadResult(
    StrategyBundleManifest Manifest,
    byte[] ManifestBytes,
    IReadOnlyDictionary<string, byte[]> Payloads,
    StrategyBundleEnvelope? Envelope,
    string ContentRootSha256,
    long CompressedLength,
    long ExpandedLength)
{
    public StrategyBundleInspection ToInspection(StrategyBundleSignatureEvidence? evidence = null) => new(
        Manifest,
        ContentRootSha256,
        evidence ?? (Envelope is null
            ? new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.Missing,
                null,
                null,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                "No publisher signature is present.")
            : new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.PresentUnverified,
                Envelope.KeyId,
                StrategyBundleArchive.PublisherPayloadType,
                StrategyBundleSignatureEvidence.PublisherAlgorithm)),
        CompressedLength,
        ExpandedLength);
}

internal static class StrategyBundleArchive
{
    public const string ManifestEntryPath = DaxStrategyBundle.ManifestEntryPath;
    public const string PublisherSignatureEntryPath = DaxStrategyBundle.PublisherSignatureEntryPath;
    public const string PublisherPayloadType = DaxStrategyBundle.PublisherSignaturePayloadType;

    private static readonly DateTimeOffset FixedZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static byte[] Write(
        byte[] manifestBytes,
        IReadOnlyDictionary<string, byte[]> payloads,
        byte[]? signatureEnvelope,
        StrategyBundleLimitOptions limits)
    {
        StrategyBundlePayloadPolicy.ValidateManagedAssemblyIdentities(payloads);

        var entries = new List<KeyValuePair<string, byte[]>>(payloads.Count + 2)
        {
            new(ManifestEntryPath, manifestBytes),
        };
        entries.AddRange(payloads);
        if (signatureEnvelope is not null)
            entries.Add(new KeyValuePair<string, byte[]>(PublisherSignatureEntryPath, signatureEnvelope));
        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        if (entries.Count > limits.MaxEntryCount)
            Limit($"The bundle exceeds the entry limit of {limits.MaxEntryCount}.");
        long totalExpanded = 0;
        foreach (var (path, content) in entries)
        {
            totalExpanded = checked(totalExpanded + content.LongLength);
            if (totalExpanded > limits.MaxTotalExpandedBytes)
                Limit($"Entry '{path}' makes the bundle exceed the total expanded-size limit.");
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            foreach (var (path, content) in entries)
            {
                if (content.LongLength > limits.MaxEntryExpandedBytes)
                    Limit($"Entry '{path}' exceeds the expanded entry limit.");
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = FixedZipTimestamp;
                entry.ExternalAttributes = 0;
                using var destination = entry.Open();
                destination.Write(content);
            }
        }

        if (output.Length > limits.MaxCompressedBundleBytes)
            Limit($"The bundle exceeds the compressed size limit of {limits.MaxCompressedBundleBytes} bytes.");
        return output.ToArray();
    }

    public static StrategyBundleReadResult Read(Stream input, StrategyBundleLimitOptions limits)
    {
        var archiveBytes = ReadLimited(input, limits.MaxCompressedBundleBytes, "compressed bundle");
        ValidateZipEnvelope(archiveBytes, limits);

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pathsByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalExpanded = 0;

        try
        {
            using var source = new MemoryStream(archiveBytes, writable: false);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: Encoding.UTF8);
            if (zip.Entries.Count == 0 || zip.Entries.Count > limits.MaxEntryCount)
                Limit($"The archive entry count must be between 1 and {limits.MaxEntryCount}.");

            foreach (var entry in zip.Entries)
            {
                var path = ValidateArchiveEntryPath(entry, limits);
                StrategyBundlePath.AddDistinctFilePath(pathsByAlias, path, "Archive entry");

                if (entry.CompressedLength < 0 || entry.CompressedLength > limits.MaxCompressedEntryBytes)
                    Limit($"Entry '{path}' exceeds the compressed entry limit.");
                if (entry.Length < 0 || entry.Length > limits.MaxEntryExpandedBytes)
                    Limit($"Entry '{path}' exceeds the expanded entry limit.");
                if (path == ManifestEntryPath && entry.Length > limits.MaxManifestBytes)
                    Limit("The manifest exceeds its size limit.");
                if (path == PublisherSignatureEntryPath && entry.Length > limits.MaxSignatureEnvelopeBytes)
                    Limit("The publisher signature envelope exceeds its size limit.");

                var ratio = entry.Length == 0
                    ? 1d
                    : entry.CompressedLength == 0
                        ? double.PositiveInfinity
                        : (double)entry.Length / entry.CompressedLength;
                if (ratio > limits.MaxCompressionRatio)
                    Limit($"Entry '{path}' exceeds the compression-ratio limit.");

                totalExpanded = checked(totalExpanded + entry.Length);
                if (totalExpanded > limits.MaxTotalExpandedBytes)
                    Limit("The archive exceeds the total expanded-size limit.");

                using var entryStream = entry.Open();
                var content = ReadLimited(entryStream, entry.Length, $"entry '{path}'");
                if (content.LongLength != entry.Length)
                    Fail(StrategyBundleValidationError.InvalidArchive, $"Entry '{path}' length does not match its ZIP metadata.");
                contents.Add(path, content);
            }
        }
        catch (StrategyBundleValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or OverflowException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidArchive,
                "The .daxstrategy archive is malformed or unsupported.",
                ex);
        }

        if (!contents.Remove(ManifestEntryPath, out var manifestBytes))
            Fail(StrategyBundleValidationError.InvalidManifest, $"The archive is missing '{ManifestEntryPath}'.");
        contents.Remove(PublisherSignatureEntryPath, out var envelopeBytes);

        var manifest = StrategyBundleManifestCodec.ParseCanonical(manifestBytes, limits);
        var descriptors = manifest.Payloads.ToDictionary(static payload => payload.Path, StringComparer.Ordinal);
        foreach (var path in contents.Keys)
            if (!descriptors.ContainsKey(path))
                Fail(StrategyBundleValidationError.PayloadMismatch, $"Archive contains unlisted entry '{path}'.");
        foreach (var descriptor in manifest.Payloads)
        {
            if (!contents.TryGetValue(descriptor.Path, out var content))
                Fail(StrategyBundleValidationError.PayloadMismatch, $"Archive is missing payload '{descriptor.Path}'.");
            if (content.LongLength != descriptor.Length)
                Fail(StrategyBundleValidationError.PayloadMismatch, $"Payload '{descriptor.Path}' has the wrong length.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash),
                    Encoding.ASCII.GetBytes(descriptor.Sha256)))
                Fail(StrategyBundleValidationError.PayloadMismatch, $"Payload '{descriptor.Path}' failed its SHA-256 check.");
            StrategyBundlePayloadPolicy.Validate(descriptor.Path, descriptor.Role, content);
        }
        StrategyBundlePayloadPolicy.ValidateManagedAssemblyGraph(manifest.ManagedAssemblies, contents);
        StrategyBundleEnginePolicy.Validate(manifest.Engine, contents[manifest.Engine.AssemblyPath]);

        StrategyBundleEnvelope? envelope = null;
        if (envelopeBytes is not null)
            envelope = ParseEnvelope(envelopeBytes, manifestBytes, limits);

        return new StrategyBundleReadResult(
            manifest,
            manifestBytes,
            contents,
            envelope,
            Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            archiveBytes.LongLength,
            totalExpanded);
    }

    public static byte[] CreateEnvelope(string keyId, byte[] manifestBytes, byte[] signature)
    {
        keyId = NormalizeKeyId(keyId);
        if (signature.Length != 64)
            throw new ArgumentException("An IEEE-P1363 P-256 signature must be exactly 64 bytes.", nameof(signature));

        var json = new StringBuilder(manifestBytes.Length * 2);
        json.Append("{\"payloadType\":");
        CanonicalJson.AppendString(json, PublisherPayloadType);
        json.Append(",\"payload\":");
        CanonicalJson.AppendString(json, Convert.ToBase64String(manifestBytes));
        json.Append(",\"signatures\":[{\"keyid\":");
        CanonicalJson.AppendString(json, keyId);
        json.Append(",\"sig\":");
        CanonicalJson.AppendString(json, Convert.ToBase64String(signature));
        json.Append("}]}");
        return CanonicalJson.ToUtf8(json);
    }

    /// <summary>DSSE PAE domain-separates the payload type and both byte lengths from the payload.</summary>
    public static byte[] CreatePreAuthenticationEncoding(ReadOnlySpan<byte> payload)
    {
        var payloadType = Encoding.UTF8.GetBytes(PublisherPayloadType);
        var prefix = Encoding.ASCII.GetBytes(
            $"DSSEv1 {payloadType.Length} {PublisherPayloadType} {payload.Length} ");
        var encoded = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(encoded, 0);
        payload.CopyTo(encoded.AsSpan(prefix.Length));
        return encoded;
    }

    private static StrategyBundleEnvelope ParseEnvelope(
        byte[] envelopeBytes,
        byte[] manifestBytes,
        StrategyBundleLimitOptions limits)
    {
        if (envelopeBytes.LongLength > limits.MaxSignatureEnvelopeBytes)
            Limit("The publisher signature envelope exceeds its size limit.");

        try
        {
            using var document = JsonDocument.Parse(envelopeBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            RequireEnvelopeProperties(root, "signature envelope", ["payloadType", "payload", "signatures"]);
            var payloadType = RequiredEnvelopeString(root, "payloadType", "signature envelope");
            if (!string.Equals(payloadType, PublisherPayloadType, StringComparison.Ordinal))
                EnvelopeFail($"Unsupported publisher signature payload type '{payloadType}'.");

            var encodedPayload = RequiredEnvelopeString(root, "payload", "signature envelope");
            var payload = Convert.FromBase64String(encodedPayload);
            if (!payload.AsSpan().SequenceEqual(manifestBytes))
                EnvelopeFail("The publisher signature envelope payload does not match the canonical manifest.");

            var signatures = root.GetProperty("signatures");
            if (signatures.ValueKind != JsonValueKind.Array || signatures.GetArrayLength() != 1)
                EnvelopeFail("The publisher signature envelope must contain exactly one signature.");
            var signatureElement = signatures[0];
            RequireEnvelopeProperties(signatureElement, "publisher signature", ["keyid", "sig"]);
            var keyId = NormalizeKeyId(RequiredEnvelopeString(signatureElement, "keyid", "publisher signature"));
            var signature = Convert.FromBase64String(RequiredEnvelopeString(signatureElement, "sig", "publisher signature"));
            if (signature.Length != 64)
                EnvelopeFail("The publisher signature is not a 64-byte IEEE-P1363 P-256 signature.");

            var canonical = CreateEnvelope(keyId, manifestBytes, signature);
            if (!envelopeBytes.AsSpan().SequenceEqual(canonical))
                EnvelopeFail("The publisher signature envelope is not in canonical JSON form.");
            return new StrategyBundleEnvelope(keyId, signature, canonical);
        }
        catch (StrategyBundleValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidSignatureEnvelope,
                "The publisher signature envelope is malformed.",
                ex);
        }
    }

    private static string ValidateArchiveEntryPath(ZipArchiveEntry entry, StrategyBundleLimitOptions limits)
    {
        var path = entry.FullName;
        if (path.Length == 0 || path.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
            Fail(StrategyBundleValidationError.InvalidPath, "Directory entries are not allowed in a strategy bundle.");

        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not 0 and not 0x8000)
            Fail(StrategyBundleValidationError.InvalidPath, $"Non-regular Unix special-file entry '{path}' is not allowed.");

        const int windowsDirectory = (int)FileAttributes.Directory;
        const int windowsDevice = (int)FileAttributes.Device;
        const int windowsReparsePoint = (int)FileAttributes.ReparsePoint;
        if ((entry.ExternalAttributes & (windowsDirectory | windowsDevice | windowsReparsePoint)) != 0)
            Fail(StrategyBundleValidationError.InvalidPath, $"Windows directory, device, or reparse-point entry '{path}' is not allowed.");

        if (path == ManifestEntryPath || path == PublisherSignatureEntryPath)
        {
            if (!string.Equals(path, path.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
                Fail(StrategyBundleValidationError.InvalidPath, $"Archive entry '{path}' is not Unicode NFC normalized.");
            return path;
        }

        return StrategyBundlePath.NormalizePayloadPath(path, limits, requireCanonical: true);
    }

    private static byte[] ReadLimited(Stream input, long maximumBytes, string label)
    {
        if (maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "This implementation requires limits no greater than Int32.MaxValue.");
        if (input.CanSeek)
        {
            var remaining = input.Length - input.Position;
            if (remaining < 0 || remaining > maximumBytes)
                Limit($"The {label} exceeds its limit of {maximumBytes} bytes.");
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
                Limit($"The {label} exceeds its limit of {maximumBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void ValidateZipEnvelope(byte[] archive, StrategyBundleLimitOptions limits)
    {
        const int eocdLength = 22;
        if (archive.Length < eocdLength + 4)
            Fail(StrategyBundleValidationError.InvalidArchive, "The file is too short to be a ZIP archive.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(archive) != 0x04034B50)
            Fail(StrategyBundleValidationError.InvalidArchive, "Self-extracting or prepended ZIP data is not allowed.");

        var eocd = archive.AsSpan(archive.Length - eocdLength, eocdLength);
        if (BinaryPrimitives.ReadUInt32LittleEndian(eocd) != 0x06054B50)
            Fail(StrategyBundleValidationError.InvalidArchive, "ZIP comments, trailing bytes, and ZIP64 archives are not allowed.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]) != 0)
            Fail(StrategyBundleValidationError.InvalidArchive, "Multi-disk ZIP archives are not allowed.");
        var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]);
        var entriesTotal = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        if (entriesOnDisk != entriesTotal || entriesTotal == 0 || entriesTotal > limits.MaxEntryCount)
            Limit("The ZIP entry count is invalid or exceeds the configured limit.");
        var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(eocd[20..]);
        if (commentLength != 0 || (long)centralOffset + centralSize != archive.Length - eocdLength)
            Fail(StrategyBundleValidationError.InvalidArchive, "The ZIP central directory is not canonical.");
    }

    private static string NormalizeKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (!string.Equals(keyId, keyId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("The publisher key ID must not contain surrounding whitespace.", nameof(keyId));
        var normalized = keyId.Normalize(NormalizationForm.FormC);
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
            throw new ArgumentException("The publisher key ID is too long or contains control characters.", nameof(keyId));
        return normalized;
    }

    private static void RequireEnvelopeProperties(JsonElement element, string location, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) EnvelopeFail($"The {location} must be an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!found.Add(property.Name)) EnvelopeFail($"The {location} contains duplicate property '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                EnvelopeFail($"The {location} contains unknown property '{property.Name}'.");
        }
        foreach (var property in expected)
            if (!found.Contains(property)) EnvelopeFail($"The {location} is missing property '{property}'.");
    }

    private static string RequiredEnvelopeString(JsonElement parent, string property, string location)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String) EnvelopeFail($"The {location}.{property} value must be a string.");
        return value.GetString()!;
    }

    [DoesNotReturn]
    private static void EnvelopeFail(string message) =>
        Fail(StrategyBundleValidationError.InvalidSignatureEnvelope, message);

    [DoesNotReturn]
    private static void Limit(string message) =>
        Fail(StrategyBundleValidationError.LimitExceeded, message);

    [DoesNotReturn]
    private static void Fail(StrategyBundleValidationError error, string message) =>
        throw new StrategyBundleValidationException(error, message);
}
