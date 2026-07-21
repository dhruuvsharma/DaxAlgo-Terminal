namespace DaxAlgo.Strategy.Bundle;

internal readonly record struct StrategyBundleSemanticVersion(
    string Major,
    string Minor,
    string Patch,
    string[]? PreRelease) : IComparable<StrategyBundleSemanticVersion>
{
    public static string Normalize(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Length > 128)
            throw new ArgumentException("The version must be a trimmed semantic version.", parameterName);
        _ = Parse(value, parameterName);
        return value;
    }

    public static StrategyBundleSemanticVersion Parse(string value, string parameterName)
    {
        var withoutBuild = value.Split('+', 2);
        if (withoutBuild.Length == 2 && !ValidIdentifiers(withoutBuild[1], numericLeadingZeroRule: false))
            throw new ArgumentException("The version contains invalid build metadata.", parameterName);

        var coreAndPreRelease = withoutBuild[0].Split('-', 2);
        var core = coreAndPreRelease[0].Split('.');
        if (core.Length != 3 || core.Any(static part => !ValidNumeric(part)))
            throw new ArgumentException("The version must contain three semantic-version core numbers.", parameterName);

        string[]? preRelease = null;
        if (coreAndPreRelease.Length == 2)
        {
            if (!ValidIdentifiers(coreAndPreRelease[1], numericLeadingZeroRule: true))
                throw new ArgumentException("The version contains invalid pre-release identifiers.", parameterName);
            preRelease = coreAndPreRelease[1].Split('.');
        }

        return new StrategyBundleSemanticVersion(core[0], core[1], core[2], preRelease);
    }

    public int CompareTo(StrategyBundleSemanticVersion other)
    {
        var comparison = CompareNumeric(Major, other.Major);
        if (comparison != 0) return comparison;
        comparison = CompareNumeric(Minor, other.Minor);
        if (comparison != 0) return comparison;
        comparison = CompareNumeric(Patch, other.Patch);
        if (comparison != 0) return comparison;

        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        var common = Math.Min(PreRelease.Length, other.PreRelease.Length);
        for (var index = 0; index < common; index++)
        {
            var left = PreRelease[index];
            var right = other.PreRelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            comparison = leftNumeric && rightNumeric
                ? CompareNumeric(left, right)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : StringComparer.Ordinal.Compare(left, right);
            if (comparison != 0) return comparison;
        }
        return PreRelease.Length.CompareTo(other.PreRelease.Length);
    }

    private static bool ValidNumeric(string value) =>
        value.Length > 0 &&
        value.All(char.IsAsciiDigit) &&
        (value.Length == 1 || value[0] != '0');

    private static bool ValidIdentifiers(string value, bool numericLeadingZeroRule) =>
        value.Length > 0 && value.Split('.').All(part =>
            part.Length > 0 &&
            part.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!numericLeadingZeroRule || !part.All(char.IsAsciiDigit) || part.Length == 1 || part[0] != '0'));

    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : StringComparer.Ordinal.Compare(left, right);
}
