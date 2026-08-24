using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DaxAlgo.Package;

/// <summary>
/// The one place a payload path is judged safe. Everything a package writes or extracts goes through
/// here, because a second normaliser would eventually disagree with this one about what a zip-slip
/// looks like — and the disagreement would be the vulnerability.
///
/// <para>Folded in from the retired bundle library on 2026-08-24, trimmed to what packages use.</para>
/// </summary>
internal static class DaxPayloadPath
{
    /// <summary>Windows opens these by name regardless of the directory, so a payload that lands on
    /// one can escape the extraction root without ever containing a traversal segment.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    public static string NormalizePayloadPath(string path, DaxPackageLimits limits, bool requireCanonical)
    {
        if (string.IsNullOrWhiteSpace(path)) Fail("Payload path is empty.");
        if (path.IndexOf('\\') >= 0) Fail($"Payload path '{path}' uses a backslash.");

        var normalized = path.Normalize(NormalizationForm.FormC);
        if (requireCanonical && !string.Equals(path, normalized, StringComparison.Ordinal))
            Fail($"Payload path '{path}' is not Unicode NFC normalized.");
        if (normalized.Length > limits.MaximumPathLength)
            Fail($"Payload path exceeds {limits.MaximumPathLength} characters.");
        if (normalized[0] == '/' || IsDriveQualified(normalized))
            Fail($"Payload path '{normalized}' is absolute.");
        if (normalized.Contains(':'))
            Fail($"Payload path '{normalized}' contains an alternate-data-stream separator.");

        var segments = normalized.Split('/');
        if (segments.Length > limits.MaximumPathDepth)
            Fail($"Payload path '{normalized}' exceeds the depth limit of {limits.MaximumPathDepth}.");
        if (segments.Length < 2 || !string.Equals(segments[0], "payload", StringComparison.Ordinal))
            Fail($"Payload path '{normalized}' must be below 'payload/'.");

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                Fail($"Payload path '{normalized}' contains an empty or traversal segment.");
            // Windows silently strips a trailing dot or space, so 'evil.' and 'evil' are the same file
            // on disk while being different strings in the manifest.
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

    private static bool IsDriveQualified(string value) =>
        value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static bool IsForbiddenCharacter(char value) =>
        char.IsControl(value) || value is '\0' or '<' or '>' or '"' or '|' or '?' or '*';

    [DoesNotReturn]
    private static void Fail(string message) =>
        throw new DaxPackageException(DaxPackageError.UnsafePath, message);
}
