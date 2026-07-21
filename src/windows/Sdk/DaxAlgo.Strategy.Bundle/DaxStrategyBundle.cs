using System.Security.Cryptography;
using System.Text;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>Creates and verifies passive .daxstrategy archives without loading any payload assembly.</summary>
public static class DaxStrategyBundle
{
    public const string FileExtension = ".daxstrategy";
    public const string ManifestEntryPath = "bundle.manifest.json";
    public const string PublisherSignatureEntryPath = "signatures/publisher.dsse.json";
    public const string PublisherSignaturePayloadType = "application/vnd.daxalgo.strategy-manifest.v1+json";

    public static StrategyBundlePackResult Pack(
        string outputPath,
        StrategyBundlePackRequest request,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var output = new MemoryStream();
        var result = Pack(output, request, limits);
        File.WriteAllBytes(outputPath, output.ToArray());
        return result;
    }

    public static StrategyBundlePackResult Pack(
        Stream output,
        StrategyBundlePackRequest request,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(request);
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));
        var checkedLimits = (limits ?? StrategyBundleLimitOptions.Default).Checked();

        var payloadBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pathsByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptors = new List<StrategyBundlePayloadDescriptor>();
        long expandedTotal = 0;

        foreach (var source in request.Payloads ?? [])
        {
            if (source is null) throw new ArgumentException("Payload sources must not contain null entries.", nameof(request));
            var path = StrategyBundlePath.NormalizePayloadPath(source.Path, checkedLimits, requireCanonical: false);
            StrategyBundlePath.AddDistinctFilePath(pathsByAlias, path, "Payload path");

            using var sourceStream = source.OpenRead();
            var bytes = ReadPayload(sourceStream, checkedLimits.MaxEntryExpandedBytes, path);
            expandedTotal = checked(expandedTotal + bytes.LongLength);
            if (expandedTotal > checkedLimits.MaxTotalExpandedBytes)
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.LimitExceeded,
                    "Payloads exceed the total expanded-size limit.");

            payloadBytes.Add(path, bytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            descriptors.Add(new StrategyBundlePayloadDescriptor(
                path,
                source.Role,
                bytes.LongLength,
                sha256));
            StrategyBundlePayloadPolicy.Validate(path, source.Role, bytes);
        }

        var managedAssemblies = StrategyBundlePayloadPolicy.DescribeManagedAssemblies(payloadBytes);
        var manifest = StrategyBundleManifestCodec.Create(request, descriptors, managedAssemblies, checkedLimits);
        _ = ResolveEngineClosure(manifest);
        StrategyBundleEnginePolicy.Validate(manifest.Engine, payloadBytes[manifest.Engine.AssemblyPath]);
        var manifestBytes = StrategyBundleManifestCodec.WriteCanonical(manifest);
        if (manifestBytes.LongLength > checkedLimits.MaxManifestBytes)
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.LimitExceeded,
                "The canonical manifest exceeds its size limit.");
        if (expandedTotal + manifestBytes.LongLength > checkedLimits.MaxTotalExpandedBytes)
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.LimitExceeded,
                "The bundle exceeds the total expanded-size limit.");

        var bundle = StrategyBundleArchive.Write(manifestBytes, payloadBytes, signatureEnvelope: null, checkedLimits);
        WriteComplete(output, bundle);
        return new StrategyBundlePackResult(
            manifest,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
            bundle.LongLength);
    }

    public static StrategyBundleSignResult Sign(
        string inputPath,
        string outputPath,
        ECDsa publisherPrivateKey,
        string keyId,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var input = File.OpenRead(inputPath);
        using var output = new MemoryStream();
        var result = Sign(input, output, publisherPrivateKey, keyId, limits);
        File.WriteAllBytes(outputPath, output.ToArray());
        return result;
    }

    public static StrategyBundleSignResult Sign(
        Stream input,
        Stream output,
        ECDsa publisherPrivateKey,
        string keyId,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(publisherPrivateKey);
        if (!input.CanRead) throw new ArgumentException("The input stream must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));
        EnsureP256(publisherPrivateKey, nameof(publisherPrivateKey));
        var normalizedKeyId = NormalizeKeyId(keyId, nameof(keyId));
        var checkedLimits = (limits ?? StrategyBundleLimitOptions.Default).Checked();
        var read = StrategyBundleArchive.Read(input, checkedLimits);
        if (read.Envelope is not null)
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidSignatureEnvelope,
                "The bundle already contains a publisher signature.");

        var pae = StrategyBundleArchive.CreatePreAuthenticationEncoding(read.ManifestBytes);
        var signature = publisherPrivateKey.SignData(
            pae,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = StrategyBundleArchive.CreateEnvelope(normalizedKeyId, read.ManifestBytes, signature);
        if (envelope.LongLength > checkedLimits.MaxSignatureEnvelopeBytes)
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.LimitExceeded,
                "The publisher signature envelope exceeds its size limit.");

        var bundle = StrategyBundleArchive.Write(read.ManifestBytes, read.Payloads, envelope, checkedLimits);
        WriteComplete(output, bundle);
        return new StrategyBundleSignResult(read.Manifest, read.ContentRootSha256, normalizedKeyId, bundle.LongLength);
    }

    public static StrategyBundleInspection Inspect(
        string bundlePath,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        using var input = File.OpenRead(bundlePath);
        return Inspect(input, limits);
    }

    public static StrategyBundleInspection Inspect(
        Stream input,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead) throw new ArgumentException("The input stream must be readable.", nameof(input));
        var checkedLimits = (limits ?? StrategyBundleLimitOptions.Default).Checked();
        return StrategyBundleArchive.Read(input, checkedLimits).ToInspection();
    }

    public static StrategyBundleVerification Verify(
        string bundlePath,
        IEnumerable<StrategyBundlePublisherKey> trustedPublisherKeys,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        using var input = File.OpenRead(bundlePath);
        return Verify(input, trustedPublisherKeys, limits);
    }

    public static StrategyBundleVerification Verify(
        Stream input,
        IEnumerable<StrategyBundlePublisherKey> trustedPublisherKeys,
        StrategyBundleLimitOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(trustedPublisherKeys);
        if (!input.CanRead) throw new ArgumentException("The input stream must be readable.", nameof(input));
        var checkedLimits = (limits ?? StrategyBundleLimitOptions.Default).Checked();
        var read = StrategyBundleArchive.Read(input, checkedLimits);

        if (read.Envelope is null)
        {
            var missing = new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.Missing,
                null,
                null,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                "No publisher signature is present.");
            return new StrategyBundleVerification(read.ToInspection(missing), missing);
        }

        var keys = new Dictionary<(string PublisherId, string KeyId), StrategyBundlePublisherKey>();
        foreach (var key in trustedPublisherKeys)
        {
            if (key is null) throw new ArgumentException("Trusted publisher keys must not contain null entries.", nameof(trustedPublisherKeys));
            var publisherId = NormalizePublisherId(key.PublisherId, nameof(trustedPublisherKeys));
            var keyId = NormalizeKeyId(key.KeyId, nameof(trustedPublisherKeys));
            var normalizedKey = key with
            {
                PublisherId = publisherId,
                KeyId = keyId,
                SubjectPublicKeyInfo = key.SubjectPublicKeyInfo.ToArray(),
            };
            if (!keys.TryAdd((publisherId, keyId), normalizedKey))
                throw new ArgumentException(
                    $"Duplicate trusted publisher/key binding '{publisherId}' / '{keyId}'.",
                    nameof(trustedPublisherKeys));
        }

        var manifestPublisherId = read.Manifest.Identity.PublisherId;
        if (!keys.TryGetValue((manifestPublisherId, read.Envelope.KeyId), out var trustedKey))
        {
            var unknown = new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.UnknownKey,
                read.Envelope.KeyId,
                StrategyBundleArchive.PublisherPayloadType,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                $"Key '{read.Envelope.KeyId}' is not trusted for manifest publisher '{manifestPublisherId}'.");
            return new StrategyBundleVerification(read.ToInspection(unknown), unknown);
        }

        StrategyBundleSignatureEvidence evidence;
        var trustedKeyBytes = trustedKey.SubjectPublicKeyInfo.ToArray();
        var keyFingerprintSha256 = Convert.ToHexStringLower(SHA256.HashData(trustedKeyBytes));
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedKeyBytes, out var bytesRead);
            if (bytesRead != trustedKeyBytes.Length)
                throw new CryptographicException("The publisher public key contains trailing data.");
            EnsureP256(key, nameof(trustedPublisherKeys));
            var pae = StrategyBundleArchive.CreatePreAuthenticationEncoding(read.ManifestBytes);
            var valid = key.VerifyData(
                pae,
                read.Envelope.Signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            evidence = new StrategyBundleSignatureEvidence(
                valid ? StrategyBundleSignatureStatus.Verified : StrategyBundleSignatureStatus.Invalid,
                read.Envelope.KeyId,
                StrategyBundleArchive.PublisherPayloadType,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                valid ? null : "The publisher signature does not verify for the canonical manifest.")
            {
                KeyFingerprintSha256 = keyFingerprintSha256,
            };
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            evidence = new StrategyBundleSignatureEvidence(
                StrategyBundleSignatureStatus.Invalid,
                read.Envelope.KeyId,
                StrategyBundleArchive.PublisherPayloadType,
                StrategyBundleSignatureEvidence.PublisherAlgorithm,
                $"The publisher key or signature is invalid: {ex.Message}")
            {
                KeyFingerprintSha256 = keyFingerprintSha256,
            };
        }

        return new StrategyBundleVerification(read.ToInspection(evidence), evidence);
    }

    /// <summary>
    /// Resolves the exact managed load set for the headless engine without loading an assembly.
    /// The engine is first and reachable private dependencies are ordered by ordinal payload path.
    /// Windows UI payloads are never valid members of this closure.
    /// </summary>
    public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ResolveEngineClosure(
        StrategyBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return StrategyBundlePayloadPolicy.ResolveEngineClosure(manifest);
    }

    private static byte[] ReadPayload(Stream source, long maximumBytes, string path)
    {
        if (maximumBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (source.CanSeek && source.Length - source.Position > maximumBytes)
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.LimitExceeded,
                $"Payload '{path}' exceeds the expanded entry limit.");

        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.LimitExceeded,
                    $"Payload '{path}' exceeds the expanded entry limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteComplete(Stream output, byte[] content)
    {
        if (output.CanSeek)
        {
            output.SetLength(output.Position);
            output.Write(content);
            output.SetLength(output.Position);
        }
        else
        {
            output.Write(content);
        }
    }

    private static void EnsureP256(ECDsa key, string parameterName)
    {
        ECParameters parameters;
        try { parameters = key.ExportParameters(includePrivateParameters: false); }
        catch (CryptographicException ex)
        {
            throw new ArgumentException("The ECDSA public parameters could not be inspected.", parameterName, ex);
        }

        if (key.KeySize != 256 || parameters.Curve.Oid.Value != "1.2.840.10045.3.1.7")
            throw new ArgumentException("Publisher keys must use the NIST P-256 curve.", parameterName);
    }

    private static string NormalizePublisherId(string publisherId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId, parameterName);
        if (!string.Equals(publisherId, publisherId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Publisher IDs must not contain surrounding whitespace.", parameterName);
        var normalized = publisherId.Normalize(NormalizationForm.FormC);
        if (normalized.Length > 128 ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            normalized.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ||
            !string.Equals(normalized, normalized.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Publisher IDs must be lowercase portable identifiers.", parameterName);
        }
        return normalized;
    }

    private static string NormalizeKeyId(string keyId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId, parameterName);
        if (!string.Equals(keyId, keyId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Publisher key IDs must not contain surrounding whitespace.", parameterName);
        var normalized = keyId.Normalize(NormalizationForm.FormC);
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
            throw new ArgumentException("Publisher key IDs are too long or contain control characters.", parameterName);
        return normalized;
    }
}
