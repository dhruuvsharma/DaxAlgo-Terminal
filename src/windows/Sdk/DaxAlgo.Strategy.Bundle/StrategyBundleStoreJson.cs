using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DaxAlgo.Strategy.Bundle;

internal sealed record StrategyBundleActivationPointer(
    string Schema,
    int SchemaVersion,
    string StrategyId,
    string ContentRootSha256,
    string ArchiveSha256)
{
    public const string CurrentSchema = "daxalgo.strategy-activation";
    public const int CurrentSchemaVersion = 1;
}

internal static class StrategyBundleStoreJson
{
    public static byte[] WriteReceipt(StrategyBundleInstallReceipt receipt)
    {
        var json = new StringBuilder(1024);
        json.Append("{\"schema\":");
        CanonicalJson.AppendString(json, receipt.Schema);
        json.Append(",\"schemaVersion\":");
        json.Append(receipt.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"contentRootSha256\":");
        CanonicalJson.AppendString(json, receipt.ContentRootSha256);
        json.Append(",\"archiveSha256\":");
        CanonicalJson.AppendString(json, receipt.ArchiveSha256);
        json.Append(",\"archiveLength\":");
        json.Append(receipt.ArchiveLength.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"identity\":{\"id\":");
        CanonicalJson.AppendString(json, receipt.Identity.Id);
        json.Append(",\"name\":");
        CanonicalJson.AppendString(json, receipt.Identity.Name);
        json.Append(",\"publisherId\":");
        CanonicalJson.AppendString(json, receipt.Identity.PublisherId);
        json.Append(",\"version\":");
        CanonicalJson.AppendString(json, receipt.Identity.Version);
        json.Append("},\"compatibility\":{\"targetSdkVersion\":");
        CanonicalJson.AppendString(json, receipt.Compatibility.TargetSdkVersion);
        json.Append(",\"minimumHostVersion\":");
        AppendNullableString(json, receipt.Compatibility.MinimumHostVersion);
        json.Append(",\"maximumHostVersion\":");
        AppendNullableString(json, receipt.Compatibility.MaximumHostVersion);
        json.Append("},\"publisherSignature\":{\"status\":");
        CanonicalJson.AppendString(json, SignatureStatusToWire(receipt.PublisherSignature.Status));
        json.Append(",\"keyId\":");
        AppendNullableString(json, receipt.PublisherSignature.KeyId);
        json.Append(",\"keyFingerprintSha256\":");
        AppendNullableString(json, receipt.PublisherSignature.KeyFingerprintSha256);
        json.Append(",\"payloadType\":");
        AppendNullableString(json, receipt.PublisherSignature.PayloadType);
        json.Append(",\"algorithm\":");
        CanonicalJson.AppendString(json, receipt.PublisherSignature.Algorithm);
        json.Append("}}");
        return CanonicalJson.ToUtf8(json);
    }

    public static StrategyBundleInstallReceipt ParseReceipt(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), StrictDocumentOptions);
            var root = document.RootElement;
            RequireProperties(
                root,
                "install receipt",
                ["schema", "schemaVersion", "contentRootSha256", "archiveSha256", "archiveLength", "identity", "compatibility", "publisherSignature"]);

            var identity = root.GetProperty("identity");
            RequireProperties(identity, "install receipt identity", ["id", "name", "publisherId", "version"]);
            var compatibility = root.GetProperty("compatibility");
            RequireProperties(
                compatibility,
                "install receipt compatibility",
                ["targetSdkVersion", "minimumHostVersion", "maximumHostVersion"]);
            var signature = root.GetProperty("publisherSignature");
            RequireProperties(
                signature,
                "install receipt publisher signature",
                ["status", "keyId", "keyFingerprintSha256", "payloadType", "algorithm"]);

            var receipt = new StrategyBundleInstallReceipt(
                RequiredString(root, "schema", "install receipt"),
                RequiredInt32(root, "schemaVersion", "install receipt"),
                RequiredString(root, "contentRootSha256", "install receipt"),
                RequiredString(root, "archiveSha256", "install receipt"),
                RequiredInt64(root, "archiveLength", "install receipt"),
                new StrategyBundleIdentity(
                    RequiredString(identity, "id", "install receipt identity"),
                    RequiredString(identity, "name", "install receipt identity"),
                    RequiredString(identity, "version", "install receipt identity"),
                    RequiredString(identity, "publisherId", "install receipt identity")),
                new StrategyBundleCompatibility(
                    RequiredString(compatibility, "targetSdkVersion", "install receipt compatibility"),
                    NullableString(compatibility, "minimumHostVersion", "install receipt compatibility"),
                    NullableString(compatibility, "maximumHostVersion", "install receipt compatibility")),
                new StrategyBundleSignatureEvidence(
                    SignatureStatusFromWire(RequiredString(signature, "status", "install receipt publisher signature")),
                    NullableString(signature, "keyId", "install receipt publisher signature"),
                    NullableString(signature, "payloadType", "install receipt publisher signature"),
                    RequiredString(signature, "algorithm", "install receipt publisher signature"))
                {
                    KeyFingerprintSha256 = NullableString(
                        signature,
                        "keyFingerprintSha256",
                        "install receipt publisher signature"),
                });

            if (receipt.Schema != StrategyBundleInstallReceipt.CurrentSchema ||
                receipt.SchemaVersion != StrategyBundleInstallReceipt.CurrentSchemaVersion ||
                !bytes.SequenceEqual(WriteReceipt(receipt)))
            {
                Corrupt("The install receipt is unsupported or is not canonical JSON.");
            }
            return receipt;
        }
        catch (StrategyBundleStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.CorruptInstallation,
                "The install receipt is malformed.",
                ex);
        }
    }

    public static byte[] WriteActivation(StrategyBundleActivationPointer activation)
    {
        var json = new StringBuilder(320);
        json.Append("{\"schema\":");
        CanonicalJson.AppendString(json, activation.Schema);
        json.Append(",\"schemaVersion\":");
        json.Append(activation.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"strategyId\":");
        CanonicalJson.AppendString(json, activation.StrategyId);
        json.Append(",\"contentRootSha256\":");
        CanonicalJson.AppendString(json, activation.ContentRootSha256);
        json.Append(",\"archiveSha256\":");
        CanonicalJson.AppendString(json, activation.ArchiveSha256);
        json.Append('}');
        return CanonicalJson.ToUtf8(json);
    }

    public static StrategyBundleActivationPointer ParseActivation(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), StrictDocumentOptions);
            var root = document.RootElement;
            RequireProperties(
                root,
                "activation pointer",
                ["schema", "schemaVersion", "strategyId", "contentRootSha256", "archiveSha256"]);
            var activation = new StrategyBundleActivationPointer(
                RequiredString(root, "schema", "activation pointer"),
                RequiredInt32(root, "schemaVersion", "activation pointer"),
                RequiredString(root, "strategyId", "activation pointer"),
                RequiredString(root, "contentRootSha256", "activation pointer"),
                RequiredString(root, "archiveSha256", "activation pointer"));
            if (activation.Schema != StrategyBundleActivationPointer.CurrentSchema ||
                activation.SchemaVersion != StrategyBundleActivationPointer.CurrentSchemaVersion ||
                !bytes.SequenceEqual(WriteActivation(activation)))
            {
                Corrupt("The activation pointer is unsupported or is not canonical JSON.");
            }
            return activation;
        }
        catch (StrategyBundleStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new StrategyBundleStoreException(
                StrategyBundleStoreError.CorruptInstallation,
                "The activation pointer is malformed.",
                ex);
        }
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private static void AppendNullableString(StringBuilder json, string? value)
    {
        if (value is null) json.Append("null");
        else CanonicalJson.AppendString(json, value);
    }

    private static string SignatureStatusToWire(StrategyBundleSignatureStatus status) => status switch
    {
        StrategyBundleSignatureStatus.Missing => "missing",
        StrategyBundleSignatureStatus.Verified => "verified",
        _ => throw new ArgumentOutOfRangeException(nameof(status), "Only accepted publisher evidence may be stored."),
    };

    private static StrategyBundleSignatureStatus SignatureStatusFromWire(string status) => status switch
    {
        "missing" => StrategyBundleSignatureStatus.Missing,
        "verified" => StrategyBundleSignatureStatus.Verified,
        _ => throw new StrategyBundleStoreException(
            StrategyBundleStoreError.CorruptInstallation,
            $"The install receipt contains unsupported publisher status '{status}'."),
    };

    private static void RequireProperties(JsonElement element, string location, IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) Corrupt($"The {location} must be an object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!found.Add(property.Name)) Corrupt($"The {location} contains duplicate property '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                Corrupt($"The {location} contains unknown property '{property.Name}'.");
        }
        foreach (var property in expected)
            if (!found.Contains(property)) Corrupt($"The {location} is missing property '{property}'.");
    }

    private static string RequiredString(JsonElement parent, string property, string location)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String) Corrupt($"The {location}.{property} value must be a string.");
        return value.GetString()!;
    }

    private static string? NullableString(JsonElement parent, string property, string location)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) Corrupt($"The {location}.{property} value must be a string or null.");
        return value.GetString();
    }

    private static int RequiredInt32(JsonElement parent, string property, string location)
    {
        if (!parent.GetProperty(property).TryGetInt32(out var value))
            Corrupt($"The {location}.{property} value must be an integer.");
        return value;
    }

    private static long RequiredInt64(JsonElement parent, string property, string location)
    {
        if (!parent.GetProperty(property).TryGetInt64(out var value))
            Corrupt($"The {location}.{property} value must be an integer.");
        return value;
    }

    [DoesNotReturn]
    private static void Corrupt(string message) =>
        throw new StrategyBundleStoreException(StrategyBundleStoreError.CorruptInstallation, message);
}
