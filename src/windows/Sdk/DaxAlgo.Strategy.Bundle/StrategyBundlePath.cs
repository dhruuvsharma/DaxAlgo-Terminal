using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DaxAlgo.Strategy.Bundle;

internal static class StrategyBundlePath
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    public static string NormalizePayloadPath(string path, StrategyBundleLimitOptions limits, bool requireCanonical)
    {
        if (string.IsNullOrWhiteSpace(path)) Fail("Payload path is empty.");
        if (path.IndexOf('\\') >= 0) Fail($"Payload path '{path}' uses a backslash.");

        var normalized = path.Normalize(NormalizationForm.FormC);
        if (requireCanonical && !string.Equals(path, normalized, StringComparison.Ordinal))
            Fail($"Payload path '{path}' is not Unicode NFC normalized.");
        if (normalized.Length > limits.MaxPathLength)
            Fail($"Payload path exceeds {limits.MaxPathLength} characters.");
        if (normalized[0] == '/' || IsDriveQualified(normalized))
            Fail($"Payload path '{normalized}' is absolute.");
        if (normalized.Contains(':'))
            Fail($"Payload path '{normalized}' contains an alternate-data-stream separator.");

        var segments = normalized.Split('/');
        if (segments.Length > limits.MaxPathDepth)
            Fail($"Payload path '{normalized}' exceeds the depth limit of {limits.MaxPathDepth}.");
        if (segments.Length < 2 || !string.Equals(segments[0], "payload", StringComparison.Ordinal))
            Fail($"Payload path '{normalized}' must be below 'payload/'.");

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                Fail($"Payload path '{normalized}' contains an empty or traversal segment.");
            if (segment[^1] is '.' or ' ')
                Fail($"Payload path '{normalized}' contains a segment ending in a dot or space.");
            if (segment.Any(IsForbiddenCharacter))
                Fail($"Payload path '{normalized}' contains a forbidden character.");

            var deviceStem = segment.Split('.', 2)[0];
            if (ReservedDeviceNames.Contains(deviceStem))
                Fail($"Payload path '{normalized}' contains reserved device name '{deviceStem}'.");
        }

        return normalized;
    }

    public static string AliasKey(string canonicalPath) =>
        canonicalPath.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    public static void AddDistinctFilePath(
        IDictionary<string, string> pathsByAlias,
        string canonicalPath,
        string scope)
    {
        var alias = AliasKey(canonicalPath);
        foreach (var existing in pathsByAlias)
        {
            if (string.Equals(alias, existing.Key, StringComparison.Ordinal))
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.DuplicatePath,
                    $"{scope} '{canonicalPath}' has a case or Unicode alias of '{existing.Value}'.");

            if (IsDescendant(alias, existing.Key) || IsDescendant(existing.Key, alias))
                throw new StrategyBundleValidationException(
                    StrategyBundleValidationError.DuplicatePath,
                    $"{scope} '{canonicalPath}' conflicts with file/descendant path '{existing.Value}'.");
        }

        pathsByAlias.Add(alias, canonicalPath);
    }

    private static bool IsDriveQualified(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsDescendant(string path, string possibleAncestor) =>
        path.Length > possibleAncestor.Length &&
        path.StartsWith(possibleAncestor, StringComparison.Ordinal) &&
        path[possibleAncestor.Length] == '/';

    private static bool IsForbiddenCharacter(char value) =>
        char.IsControl(value) || value is '\0' or '<' or '>' or '"' or '|' or '?' or '*';

    [DoesNotReturn]
    private static void Fail(string message) =>
        throw new StrategyBundleValidationException(StrategyBundleValidationError.InvalidPath, message);
}
