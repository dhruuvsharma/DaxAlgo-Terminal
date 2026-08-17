using System.Text;
using System.Text.Json;

using DaxAlgo.Strategy.Bundle;

namespace DaxAlgo.Package;

/// <summary>
/// Serialises the manifest as canonical JSON — properties in a fixed order, a frozen string-escape
/// algorithm, no incidental whitespace — so the same manifest always produces the same bytes and
/// therefore the same digest. The encoder is shared with the strategy-bundle format rather than
/// reimplemented; see the csproj for why.
/// </summary>
internal static class DaxPackageManifestCodec
{
    public static byte[] Write(DaxPackageManifest manifest)
    {
        var json = new StringBuilder();
        json.Append('{');
        Property(json, "format", manifest.Format, first: true);
        NumberProperty(json, "formatVersion", manifest.FormatVersion);
        Property(json, "kind", KindText(manifest.Kind));
        Property(json, "id", manifest.Id);
        Property(json, "version", manifest.Version);
        Property(json, "displayName", manifest.DisplayName);
        if (manifest.Description is { Length: > 0 }) Property(json, "description", manifest.Description);
        if (manifest.Publisher is { Length: > 0 }) Property(json, "publisher", manifest.Publisher);
        Property(json, "entryTypeName", manifest.EntryTypeName);

        json.Append(",\"payloads\":[");
        for (var i = 0; i < manifest.Payloads.Count; i++)
        {
            var p = manifest.Payloads[i];
            if (i > 0) json.Append(',');
            json.Append('{');
            Property(json, "path", p.Path, first: true);
            Property(json, "role", RoleText(p.Role));
            NumberProperty(json, "length", p.Length);
            Property(json, "sha256", p.Sha256);
            json.Append('}');
        }

        json.Append("]}");
        return CanonicalJson.ToUtf8(json);
    }

    public static DaxPackageManifest Read(byte[] utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;

            var payloads = new List<DaxPayloadDescriptor>();
            if (root.TryGetProperty("payloads", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                    payloads.Add(new DaxPayloadDescriptor(
                        RequiredString(element, "path"),
                        ParseRole(RequiredString(element, "role")),
                        element.GetProperty("length").GetInt64(),
                        RequiredString(element, "sha256")));
            }

            return new DaxPackageManifest(
                RequiredString(root, "format"),
                root.GetProperty("formatVersion").GetInt32(),
                ParseKind(RequiredString(root, "kind")),
                RequiredString(root, "id"),
                RequiredString(root, "version"),
                RequiredString(root, "displayName"),
                Optional(root, "description"),
                Optional(root, "publisher"),
                RequiredString(root, "entryTypeName"),
                payloads);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"The manifest is malformed: {ex.Message}");
        }
    }

    // Spelled out rather than derived from the enum name, so renaming a C# member can never silently
    // change the wire format.
    private static string KindText(DaxPackageKind kind) => kind switch
    {
        DaxPackageKind.Strategy => "strategy",
        DaxPackageKind.Visualizer => "visualizer",
        _ => throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"Unknown kind '{kind}'."),
    };

    private static DaxPackageKind ParseKind(string text) => text switch
    {
        "strategy" => DaxPackageKind.Strategy,
        "visualizer" => DaxPackageKind.Visualizer,
        _ => throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"Unknown kind '{text}'."),
    };

    private static string RoleText(DaxPayloadRole role) => role switch
    {
        DaxPayloadRole.Source => "source",
        DaxPayloadRole.Ui => "ui",
        DaxPayloadRole.Assembly => "assembly",
        DaxPayloadRole.Dependency => "dependency",
        DaxPayloadRole.Resource => "resource",
        DaxPayloadRole.Sbom => "sbom",
        DaxPayloadRole.Provenance => "provenance",
        _ => throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"Unknown role '{role}'."),
    };

    private static DaxPayloadRole ParseRole(string text) => text switch
    {
        "source" => DaxPayloadRole.Source,
        "ui" => DaxPayloadRole.Ui,
        "assembly" => DaxPayloadRole.Assembly,
        "dependency" => DaxPayloadRole.Dependency,
        "resource" => DaxPayloadRole.Resource,
        "sbom" => DaxPayloadRole.Sbom,
        "provenance" => DaxPayloadRole.Provenance,
        _ => throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"Unknown payload role '{text}'."),
    };

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new DaxPackageException(DaxPackageError.ManifestMalformed, $"'{name}' is required.");

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Property(StringBuilder json, string name, string value, bool first = false)
    {
        if (!first) json.Append(',');
        CanonicalJson.AppendString(json, name);
        json.Append(':');
        CanonicalJson.AppendString(json, value);
    }

    private static void NumberProperty(StringBuilder json, string name, long value)
    {
        json.Append(',');
        CanonicalJson.AppendString(json, name);
        json.Append(':').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
