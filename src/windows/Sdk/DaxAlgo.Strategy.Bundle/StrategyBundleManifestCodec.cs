using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace DaxAlgo.Strategy.Bundle;

internal static class StrategyBundleManifestCodec
{
    public static StrategyBundleManifest Create(
        StrategyBundlePackRequest request,
        IEnumerable<StrategyBundlePayloadDescriptor> payloads,
        IEnumerable<StrategyBundleManagedAssemblyDescriptor> managedAssemblies,
        StrategyBundleLimitOptions limits) =>
        NormalizeAndValidate(new StrategyBundleManifest(
            StrategyBundleManifest.CurrentFormat,
            StrategyBundleManifest.CurrentFormatVersion,
            request.Identity,
            request.Compatibility,
            request.Engine,
            managedAssemblies.ToArray(),
            request.Capabilities?.ToArray() ?? [],
            payloads.ToArray()), limits);

    public static byte[] WriteCanonical(StrategyBundleManifest manifest)
    {
        var json = new StringBuilder(1024);
        json.Append("{\"format\":");
        CanonicalJson.AppendString(json, manifest.Format);
        json.Append(",\"formatVersion\":");
        json.Append(manifest.FormatVersion.ToString(CultureInfo.InvariantCulture));

        json.Append(",\"identity\":{\"id\":");
        CanonicalJson.AppendString(json, manifest.Identity.Id);
        json.Append(",\"name\":");
        CanonicalJson.AppendString(json, manifest.Identity.Name);
        json.Append(",\"publisherId\":");
        CanonicalJson.AppendString(json, manifest.Identity.PublisherId);
        json.Append(",\"version\":");
        CanonicalJson.AppendString(json, manifest.Identity.Version);
        json.Append('}');

        json.Append(",\"compatibility\":{\"targetSdkVersion\":");
        CanonicalJson.AppendString(json, manifest.Compatibility.TargetSdkVersion);
        json.Append(",\"minimumHostVersion\":");
        AppendNullableString(json, manifest.Compatibility.MinimumHostVersion);
        json.Append(",\"maximumHostVersion\":");
        AppendNullableString(json, manifest.Compatibility.MaximumHostVersion);
        json.Append('}');

        json.Append(",\"engine\":{\"assemblyPath\":");
        CanonicalJson.AppendString(json, manifest.Engine.AssemblyPath);
        json.Append(",\"typeName\":");
        CanonicalJson.AppendString(json, manifest.Engine.TypeName);
        json.Append(",\"contract\":");
        CanonicalJson.AppendString(json, manifest.Engine.Contract);
        json.Append(",\"activation\":");
        CanonicalJson.AppendString(json, manifest.Engine.Activation);
        json.Append('}');

        json.Append(",\"managedAssemblies\":[");
        for (var index = 0; index < manifest.ManagedAssemblies.Count; index++)
        {
            if (index > 0) json.Append(',');
            var assembly = manifest.ManagedAssemblies[index];
            json.Append("{\"path\":");
            CanonicalJson.AppendString(json, assembly.Path);
            json.Append(",\"name\":");
            CanonicalJson.AppendString(json, assembly.Name);
            json.Append(",\"references\":[");
            for (var referenceIndex = 0; referenceIndex < assembly.References.Count; referenceIndex++)
            {
                if (referenceIndex > 0) json.Append(',');
                CanonicalJson.AppendString(json, assembly.References[referenceIndex]);
            }
            json.Append("]}");
        }
        json.Append(']');

        json.Append(",\"capabilities\":[");
        for (var index = 0; index < manifest.Capabilities.Count; index++)
        {
            if (index > 0) json.Append(',');
            CanonicalJson.AppendString(json, manifest.Capabilities[index]);
        }
        json.Append(']');

        json.Append(",\"payloads\":[");
        for (var index = 0; index < manifest.Payloads.Count; index++)
        {
            if (index > 0) json.Append(',');
            var payload = manifest.Payloads[index];
            json.Append("{\"path\":");
            CanonicalJson.AppendString(json, payload.Path);
            json.Append(",\"role\":");
            CanonicalJson.AppendString(json, RoleToWire(payload.Role));
            json.Append(",\"length\":");
            json.Append(payload.Length.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"sha256\":");
            CanonicalJson.AppendString(json, payload.Sha256);
            json.Append('}');
        }
        json.Append("]}");
        return CanonicalJson.ToUtf8(json);
    }

    public static StrategyBundleManifest ParseCanonical(
        ReadOnlyMemory<byte> bytes,
        StrategyBundleLimitOptions limits)
    {
        if (bytes.Length == 0 || bytes.Length > limits.MaxManifestBytes)
            Fail(StrategyBundleValidationError.LimitExceeded, "The bundle manifest is empty or exceeds its size limit.");

        StrategyBundleManifest parsed;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            RequireObject(root, "manifest");
            RequireProperties(root, "manifest", ["format", "formatVersion", "identity", "compatibility", "engine", "managedAssemblies", "capabilities", "payloads"]);

            var format = RequiredString(root, "format", "manifest");
            if (!string.Equals(format, StrategyBundleManifest.CurrentFormat, StringComparison.Ordinal))
                Fail(StrategyBundleValidationError.UnsupportedFormat, $"Unsupported bundle format '{format}'.");

            var formatVersionElement = root.GetProperty("formatVersion");
            if (!formatVersionElement.TryGetInt32(out var formatVersion))
                Fail(StrategyBundleValidationError.InvalidManifest, "Manifest formatVersion must be an integer.");
            if (formatVersion != StrategyBundleManifest.CurrentFormatVersion)
                Fail(StrategyBundleValidationError.UnsupportedVersion, $"Unsupported bundle format version {formatVersion}.");

            var identityElement = root.GetProperty("identity");
            RequireObject(identityElement, "identity");
            RequireProperties(identityElement, "identity", ["id", "name", "publisherId", "version"]);
            var identity = new StrategyBundleIdentity(
                RequiredString(identityElement, "id", "identity"),
                RequiredString(identityElement, "name", "identity"),
                RequiredString(identityElement, "version", "identity"),
                RequiredString(identityElement, "publisherId", "identity"));

            var compatibilityElement = root.GetProperty("compatibility");
            RequireObject(compatibilityElement, "compatibility");
            RequireProperties(compatibilityElement, "compatibility", ["targetSdkVersion", "minimumHostVersion", "maximumHostVersion"]);
            var compatibility = new StrategyBundleCompatibility(
                RequiredString(compatibilityElement, "targetSdkVersion", "compatibility"),
                NullableString(compatibilityElement, "minimumHostVersion", "compatibility"),
                NullableString(compatibilityElement, "maximumHostVersion", "compatibility"));

            var engineElement = root.GetProperty("engine");
            RequireObject(engineElement, "engine");
            RequireProperties(engineElement, "engine", ["assemblyPath", "typeName", "contract", "activation"]);
            var engine = new StrategyBundleEngineEntryPoint(
                RequiredString(engineElement, "assemblyPath", "engine"),
                RequiredString(engineElement, "typeName", "engine"),
                RequiredString(engineElement, "contract", "engine"),
                RequiredString(engineElement, "activation", "engine"));

            var managedAssembliesElement = root.GetProperty("managedAssemblies");
            if (managedAssembliesElement.ValueKind != JsonValueKind.Array)
                Fail(StrategyBundleValidationError.InvalidManifest, "Manifest managedAssemblies must be an array.");
            var managedAssemblies = new List<StrategyBundleManagedAssemblyDescriptor>();
            var assemblyIndex = 0;
            foreach (var item in managedAssembliesElement.EnumerateArray())
            {
                var location = $"managedAssemblies[{assemblyIndex++}]";
                RequireObject(item, location);
                RequireProperties(item, location, ["path", "name", "references"]);
                var referencesElement = item.GetProperty("references");
                if (referencesElement.ValueKind != JsonValueKind.Array)
                    Fail(StrategyBundleValidationError.InvalidManifest, $"{location}.references must be an array.");
                var references = referencesElement.EnumerateArray()
                    .Select((reference, index) => StringValue(reference, $"{location}.references[{index}]"))
                    .ToArray();
                managedAssemblies.Add(new StrategyBundleManagedAssemblyDescriptor(
                    RequiredString(item, "path", location),
                    RequiredString(item, "name", location),
                    references));
            }

            var capabilitiesElement = root.GetProperty("capabilities");
            if (capabilitiesElement.ValueKind != JsonValueKind.Array)
                Fail(StrategyBundleValidationError.InvalidManifest, "Manifest capabilities must be an array.");
            var capabilities = capabilitiesElement.EnumerateArray()
                .Select((item, index) => StringValue(item, $"capabilities[{index}]"))
                .ToArray();

            var payloadsElement = root.GetProperty("payloads");
            if (payloadsElement.ValueKind != JsonValueKind.Array)
                Fail(StrategyBundleValidationError.InvalidManifest, "Manifest payloads must be an array.");
            var payloads = new List<StrategyBundlePayloadDescriptor>();
            var payloadIndex = 0;
            foreach (var item in payloadsElement.EnumerateArray())
            {
                var location = $"payloads[{payloadIndex++}]";
                RequireObject(item, location);
                RequireProperties(item, location, ["path", "role", "length", "sha256"]);
                var lengthElement = item.GetProperty("length");
                if (!lengthElement.TryGetInt64(out var length))
                    Fail(StrategyBundleValidationError.InvalidManifest, $"{location}.length must be an integer.");
                payloads.Add(new StrategyBundlePayloadDescriptor(
                    RequiredString(item, "path", location),
                    RoleFromWire(RequiredString(item, "role", location)),
                    length,
                    RequiredString(item, "sha256", location)));
            }

            parsed = new StrategyBundleManifest(
                format,
                formatVersion,
                identity,
                compatibility,
                engine,
                managedAssemblies,
                capabilities,
                payloads);
        }
        catch (JsonException ex)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidManifest,
                "The bundle manifest is not valid JSON.",
                ex);
        }

        var normalized = NormalizeAndValidate(parsed, limits);
        var canonical = WriteCanonical(normalized);
        if (!bytes.Span.SequenceEqual(canonical))
            Fail(StrategyBundleValidationError.InvalidManifest, "The bundle manifest is not in canonical JSON form.");
        return normalized;
    }

    public static StrategyBundleManifest NormalizeAndValidate(
        StrategyBundleManifest manifest,
        StrategyBundleLimitOptions limits)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Format, StrategyBundleManifest.CurrentFormat, StringComparison.Ordinal))
            Fail(StrategyBundleValidationError.UnsupportedFormat, $"Unsupported bundle format '{manifest.Format}'.");
        if (manifest.FormatVersion != StrategyBundleManifest.CurrentFormatVersion)
            Fail(StrategyBundleValidationError.UnsupportedVersion, $"Unsupported bundle format version {manifest.FormatVersion}.");

        var identity = new StrategyBundleIdentity(
            NormalizeIdentifier(manifest.Identity?.Id, "identity.id"),
            NormalizeDisplayName(manifest.Identity?.Name, "identity.name"),
            NormalizeVersion(manifest.Identity?.Version, "identity.version"),
            NormalizeIdentifier(manifest.Identity?.PublisherId, "identity.publisherId"));
        var compatibility = new StrategyBundleCompatibility(
            NormalizeVersion(manifest.Compatibility?.TargetSdkVersion, "compatibility.targetSdkVersion"),
            NormalizeOptionalVersion(manifest.Compatibility?.MinimumHostVersion, "compatibility.minimumHostVersion"),
            NormalizeOptionalVersion(manifest.Compatibility?.MaximumHostVersion, "compatibility.maximumHostVersion"));
        if (compatibility.MinimumHostVersion is not null &&
            compatibility.MaximumHostVersion is not null &&
            CompareSemanticVersions(compatibility.MinimumHostVersion, compatibility.MaximumHostVersion) > 0)
        {
            Fail(
                StrategyBundleValidationError.InvalidManifest,
                "compatibility.minimumHostVersion must not exceed compatibility.maximumHostVersion.");
        }
        if (manifest.Engine is null)
            Fail(StrategyBundleValidationError.InvalidManifest, "engine is required.");
        var engine = new StrategyBundleEngineEntryPoint(
            StrategyBundlePath.NormalizePayloadPath(manifest.Engine.AssemblyPath, limits, requireCanonical: false),
            NormalizeEngineTypeName(manifest.Engine.TypeName),
            NormalizeClosedEngineValue(
                manifest.Engine.Contract,
                "engine.contract",
                StrategyBundleEngineEntryPoint.CurrentContract),
            NormalizeClosedEngineValue(
                manifest.Engine.Activation,
                "engine.activation",
                StrategyBundleEngineEntryPoint.CurrentActivation));

        var managedAssemblyPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var managedAssemblyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managedAssemblies = new List<StrategyBundleManagedAssemblyDescriptor>();
        foreach (var assembly in manifest.ManagedAssemblies ?? [])
        {
            var path = StrategyBundlePath.NormalizePayloadPath(assembly.Path, limits, requireCanonical: false);
            StrategyBundlePath.AddDistinctFilePath(managedAssemblyPaths, path, "Managed assembly path");
            var name = NormalizeAssemblyName(assembly.Name, "managed assembly name");
            if (!managedAssemblyNames.TryAdd(name, path))
                Fail(
                    StrategyBundleValidationError.InvalidPayloadSet,
                    $"Managed assembly '{path}' duplicates identity '{name}' from '{managedAssemblyNames[name]}'.");
            var references = (assembly.References ?? [])
                .Select(reference => NormalizeAssemblyName(reference, $"managed assembly '{path}' reference"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static reference => reference, StringComparer.Ordinal)
                .ToArray();
            managedAssemblies.Add(new StrategyBundleManagedAssemblyDescriptor(path, name, references));
        }
        managedAssemblies.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        var capabilities = (manifest.Capabilities ?? [])
            .Select(NormalizeCapability)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        if (capabilities.Length > limits.MaxCapabilities)
            Fail(StrategyBundleValidationError.LimitExceeded, $"The manifest exceeds the capability limit of {limits.MaxCapabilities}.");

        var pathsByAlias = new Dictionary<string, string>(StringComparer.Ordinal);
        var payloads = new List<StrategyBundlePayloadDescriptor>();
        foreach (var payload in manifest.Payloads ?? [])
        {
            if (!Enum.IsDefined(payload.Role))
                Fail(StrategyBundleValidationError.InvalidManifest, $"Payload '{payload.Path}' has an unknown role.");
            var path = StrategyBundlePath.NormalizePayloadPath(payload.Path, limits, requireCanonical: false);
            ValidateRolePath(path, payload.Role);
            StrategyBundlePath.AddDistinctFilePath(pathsByAlias, path, "Payload path");
            if (payload.Length < 0 || payload.Length > limits.MaxEntryExpandedBytes)
                Fail(StrategyBundleValidationError.LimitExceeded, $"Payload '{path}' has an invalid length.");
            if (!IsLowerHexSha256(payload.Sha256))
                Fail(StrategyBundleValidationError.InvalidManifest, $"Payload '{path}' has an invalid SHA-256 value.");
            payloads.Add(new StrategyBundlePayloadDescriptor(path, payload.Role, payload.Length, payload.Sha256));
        }

        payloads.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        if (payloads.Count + 2 > limits.MaxEntryCount)
            Fail(StrategyBundleValidationError.LimitExceeded, $"The bundle exceeds the entry limit of {limits.MaxEntryCount}.");
        if (payloads.Count(static payload => payload.Role == StrategyBundlePayloadRole.Engine) != 1)
            Fail(StrategyBundleValidationError.InvalidPayloadSet, "A strategy bundle must contain exactly one engine payload.");
        if (payloads.Count(static payload => payload.Role == StrategyBundlePayloadRole.WindowsUi) > 1)
            Fail(StrategyBundleValidationError.InvalidPayloadSet, "A strategy bundle may contain at most one Windows UI payload.");
        var enginePayload = payloads.Single(static payload => payload.Role == StrategyBundlePayloadRole.Engine);
        if (enginePayload.Length == 0)
            Fail(StrategyBundleValidationError.InvalidPayloadSet, "The engine payload must not be empty.");
        if (!string.Equals(engine.AssemblyPath, enginePayload.Path, StringComparison.Ordinal))
            Fail(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Engine entry point assembly '{engine.AssemblyPath}' does not match the engine payload '{enginePayload.Path}'.");
        var managedPayloadPaths = payloads
            .Where(static payload => payload.Role is StrategyBundlePayloadRole.Engine or
                StrategyBundlePayloadRole.WindowsUi or
                StrategyBundlePayloadRole.ManagedDependency)
            .Select(static payload => payload.Path)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (!managedAssemblies.Select(static assembly => assembly.Path)
                .SequenceEqual(managedPayloadPaths, StringComparer.Ordinal))
        {
            Fail(
                StrategyBundleValidationError.InvalidPayloadSet,
                "managedAssemblies must describe every managed payload exactly once and no other path.");
        }

        return new StrategyBundleManifest(
            StrategyBundleManifest.CurrentFormat,
            StrategyBundleManifest.CurrentFormatVersion,
            identity,
            compatibility,
            engine,
            managedAssemblies.ToArray(),
            capabilities,
            payloads.ToArray());
    }

    public static string RoleToWire(StrategyBundlePayloadRole role) => role switch
    {
        StrategyBundlePayloadRole.Engine => "engine",
        StrategyBundlePayloadRole.WindowsUi => "windows-ui",
        StrategyBundlePayloadRole.ManagedDependency => "managed-dependency",
        StrategyBundlePayloadRole.Resource => "resource",
        StrategyBundlePayloadRole.Sbom => "sbom",
        StrategyBundlePayloadRole.Provenance => "provenance",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static StrategyBundlePayloadRole RoleFromWire(string value) => value switch
    {
        "engine" => StrategyBundlePayloadRole.Engine,
        "windows-ui" => StrategyBundlePayloadRole.WindowsUi,
        "managed-dependency" => StrategyBundlePayloadRole.ManagedDependency,
        "resource" => StrategyBundlePayloadRole.Resource,
        "sbom" => StrategyBundlePayloadRole.Sbom,
        "provenance" => StrategyBundlePayloadRole.Provenance,
        _ => throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidManifest,
            $"Unknown payload role '{value}'."),
    };

    private static void RequireObject(JsonElement element, string location)
    {
        if (element.ValueKind != JsonValueKind.Object)
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} must be an object.");
    }

    private static void RequireProperties(JsonElement element, string location, IReadOnlyCollection<string> expected)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!found.Add(property.Name))
                Fail(StrategyBundleValidationError.InvalidManifest, $"{location} contains duplicate property '{property.Name}'.");
            if (!expected.Contains(property.Name, StringComparer.Ordinal))
                Fail(StrategyBundleValidationError.InvalidManifest, $"{location} contains unknown property '{property.Name}'.");
        }

        foreach (var property in expected)
            if (!found.Contains(property))
                Fail(StrategyBundleValidationError.InvalidManifest, $"{location} is missing property '{property}'.");
    }

    private static string RequiredString(JsonElement parent, string property, string location) =>
        StringValue(parent.GetProperty(property), $"{location}.{property}");

    private static string StringValue(JsonElement element, string location)
    {
        if (element.ValueKind != JsonValueKind.String)
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} must be a string.");
        return element.GetString()!;
    }

    private static string? NullableString(JsonElement parent, string property, string location)
    {
        var value = parent.GetProperty(property);
        if (value.ValueKind == JsonValueKind.Null) return null;
        return StringValue(value, $"{location}.{property}");
    }

    private static string NormalizeIdentifier(string? value, string location)
    {
        var normalized = NormalizeText(value, location, 128);
        if (!char.IsAsciiLetterOrDigit(normalized[0]) || normalized.Any(static c =>
                !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} is not a portable identifier.");
        if (!string.Equals(normalized, normalized.ToLowerInvariant(), StringComparison.Ordinal))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} must be lowercase.");
        return normalized;
    }

    private static string NormalizeDisplayName(string? value, string location) =>
        NormalizeText(value, location, 200);

    private static string NormalizeAssemblyName(string? value, string location)
    {
        var normalized = NormalizeText(value, location, 256);
        if (!(char.IsAsciiLetterOrDigit(normalized[0]) || normalized[0] == '_') ||
            normalized.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
        {
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} is not a portable assembly name.");
        }
        return normalized;
    }

    private static string NormalizeEngineTypeName(string? value)
    {
        var normalized = NormalizeText(value, "engine.typeName", 512);
        var segments = normalized.Split('.');
        if (segments.Length < 2 || segments.Any(static segment =>
                segment.Length == 0 ||
                !(char.IsAsciiLetter(segment[0]) || segment[0] == '_') ||
                segment.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
        {
            Fail(
                StrategyBundleValidationError.InvalidManifest,
                "engine.typeName must be a non-nested, fully qualified portable .NET type name.");
        }
        return normalized;
    }

    private static string NormalizeClosedEngineValue(string? value, string location, string expected)
    {
        var normalized = NormalizeText(value, location, 128);
        if (!string.Equals(normalized, expected, StringComparison.Ordinal))
            Fail(StrategyBundleValidationError.UnsupportedVersion, $"Unsupported {location} '{normalized}'.");
        return normalized;
    }

    private static string NormalizeVersion(string? value, string location)
    {
        var normalized = NormalizeText(value, location, 128);
        if (!LooksLikeSemanticVersion(normalized))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} must be a semantic version.");
        return normalized;
    }

    private static string? NormalizeOptionalVersion(string? value, string location) =>
        string.IsNullOrEmpty(value) ? null : NormalizeVersion(value, location);

    private static string NormalizeCapability(string value)
    {
        var normalized = NormalizeText(value, "capability", 128);
        if (!char.IsAsciiLetterOrDigit(normalized[0]) || normalized.Any(static c =>
                !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')))
            Fail(StrategyBundleValidationError.InvalidManifest, $"Capability '{normalized}' is not a portable identifier.");
        if (!string.Equals(normalized, normalized.ToLowerInvariant(), StringComparison.Ordinal))
            Fail(StrategyBundleValidationError.InvalidManifest, $"Capability '{normalized}' must be lowercase.");
        return normalized;
    }

    private static string NormalizeText(string? value, string location, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} is required.");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} must not contain surrounding whitespace.");
        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidManifest,
                $"{location} contains invalid Unicode.");
        }
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
            Fail(StrategyBundleValidationError.InvalidManifest, $"{location} exceeds its limit or contains control characters.");
        return normalized;
    }

    private static bool LooksLikeSemanticVersion(string value)
    {
        var plus = value.IndexOf('+');
        var coreAndPre = plus < 0 ? value : value[..plus];
        var build = plus < 0 ? null : value[(plus + 1)..];
        if (build is not null && !ValidDotIdentifiers(build)) return false;

        var dash = coreAndPre.IndexOf('-');
        var core = dash < 0 ? coreAndPre : coreAndPre[..dash];
        var pre = dash < 0 ? null : coreAndPre[(dash + 1)..];
        if (pre is not null && !ValidPreReleaseIdentifiers(pre)) return false;

        var parts = core.Split('.');
        return parts.Length == 3 && parts.All(static part =>
            part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0'));
    }

    private static bool ValidDotIdentifiers(string value) =>
        value.Length > 0 && value.Split('.').All(static part =>
            part.Length > 0 && part.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static bool ValidPreReleaseIdentifiers(string value) =>
        ValidDotIdentifiers(value) && value.Split('.').All(static part =>
            !part.All(char.IsAsciiDigit) || part.Length == 1 || part[0] != '0');

    private static int CompareSemanticVersions(string left, string right)
    {
        var leftParsed = SplitSemanticVersion(left);
        var rightParsed = SplitSemanticVersion(right);
        for (var index = 0; index < 3; index++)
        {
            var comparison = CompareNumericIdentifier(leftParsed.Core[index], rightParsed.Core[index]);
            if (comparison != 0) return comparison;
        }

        if (leftParsed.PreRelease is null) return rightParsed.PreRelease is null ? 0 : 1;
        if (rightParsed.PreRelease is null) return -1;
        var common = Math.Min(leftParsed.PreRelease.Length, rightParsed.PreRelease.Length);
        for (var index = 0; index < common; index++)
        {
            var leftPart = leftParsed.PreRelease[index];
            var rightPart = rightParsed.PreRelease[index];
            var leftNumeric = leftPart.All(char.IsAsciiDigit);
            var rightNumeric = rightPart.All(char.IsAsciiDigit);
            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = CompareNumericIdentifier(leftPart, rightPart);
            else if (leftNumeric)
                comparison = -1;
            else if (rightNumeric)
                comparison = 1;
            else
                comparison = StringComparer.Ordinal.Compare(leftPart, rightPart);
            if (comparison != 0) return comparison;
        }
        return leftParsed.PreRelease.Length.CompareTo(rightParsed.PreRelease.Length);
    }

    private static (string[] Core, string[]? PreRelease) SplitSemanticVersion(string value)
    {
        var withoutBuild = value.Split('+', 2)[0];
        var pieces = withoutBuild.Split('-', 2);
        return (pieces[0].Split('.'), pieces.Length == 1 ? null : pieces[1].Split('.'));
    }

    private static int CompareNumericIdentifier(string left, string right) =>
        left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : StringComparer.Ordinal.Compare(left, right);

    private static bool IsLowerHexSha256(string value) =>
        value is { Length: 64 } && value.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    private static void ValidateRolePath(string path, StrategyBundlePayloadRole role)
    {
        var prefix = role switch
        {
            StrategyBundlePayloadRole.Engine => "payload/engine/",
            StrategyBundlePayloadRole.WindowsUi => "payload/windows/",
            StrategyBundlePayloadRole.ManagedDependency => "payload/deps/",
            StrategyBundlePayloadRole.Resource => "payload/resources/",
            StrategyBundlePayloadRole.Sbom => "payload/sbom/",
            StrategyBundlePayloadRole.Provenance => "payload/provenance/",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length)
            Fail(StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' with role '{RoleToWire(role)}' requires a path below '{prefix}'.");

        if ((role is StrategyBundlePayloadRole.Engine or
            StrategyBundlePayloadRole.WindowsUi or
            StrategyBundlePayloadRole.ManagedDependency) &&
            !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            Fail(StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' with role '{RoleToWire(role)}' requires a .dll entry.");
    }

    private static void AppendNullableString(StringBuilder json, string? value)
    {
        if (value is null) json.Append("null");
        else CanonicalJson.AppendString(json, value);
    }

    [DoesNotReturn]
    private static void Fail(StrategyBundleValidationError error, string message) =>
        throw new StrategyBundleValidationException(error, message);
}
