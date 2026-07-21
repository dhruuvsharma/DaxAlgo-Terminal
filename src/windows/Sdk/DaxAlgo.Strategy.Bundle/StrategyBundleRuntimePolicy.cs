using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>
/// Passive validation helpers shared by an installer, job-image preparer, and isolated runtime.
/// These methods inspect metadata and hashes only; they never load strategy assemblies.
/// </summary>
public static class StrategyBundleRuntimePolicy
{
    public static StrategyBundleManifest ParseCanonicalManifest(
        ReadOnlyMemory<byte> canonicalBytes,
        StrategyBundleLimitOptions? limits = null) =>
        StrategyBundleManifestCodec.ParseCanonical(
            canonicalBytes,
            (limits ?? StrategyBundleLimitOptions.Default).Checked());

    public static string ComputeContentRoot(ReadOnlySpan<byte> canonicalManifestBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(canonicalManifestBytes));

    /// <summary>
    /// Revalidates an extracted engine image against its manifest. The supplied payload set must be
    /// exactly the resolved engine closure; unrelated dependencies and Windows UI are rejected.
    /// </summary>
    public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ValidateEngineImage(
        StrategyBundleManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloads);
        var closure = DaxStrategyBundle.ResolveEngineClosure(manifest);
        var expectedPaths = closure
            .Select(static assembly => assembly.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (payloads.Count != expectedPaths.Count || payloads.Keys.Any(path => !expectedPaths.Contains(path)))
            Reject("The staged engine image does not contain exactly the manifest-resolved engine closure.");

        foreach (var expected in closure)
        {
            if (!payloads.TryGetValue(expected.Path, out var bytes))
                Reject($"The staged engine image is missing '{expected.Path}'.");
            if (bytes.LongLength != expected.Length)
                Reject($"The staged assembly '{expected.Path}' has the wrong length.");
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actualHash),
                    System.Text.Encoding.ASCII.GetBytes(expected.Sha256)))
                Reject($"The staged assembly '{expected.Path}' failed its SHA-256 check.");
            StrategyBundlePayloadPolicy.Validate(expected.Path, expected.Role, bytes);
        }

        var actualByPath = StrategyBundlePayloadPolicy.DescribeManagedAssemblies(payloads)
            .ToDictionary(static assembly => assembly.Path, StringComparer.Ordinal);
        foreach (var expected in closure)
        {
            if (!actualByPath.TryGetValue(expected.Path, out var actual) ||
                !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !actual.References.SequenceEqual(expected.References, StringComparer.Ordinal))
            {
                Reject($"The staged assembly '{expected.Path}' does not match its manifest metadata.");
            }
        }

        StrategyBundleEnginePolicy.Validate(
            manifest.Engine,
            payloads[manifest.Engine.AssemblyPath]);
        return closure;
    }

    /// <summary>The frozen v1 framework/host assembly allowlist used by graph and runtime resolution.</summary>
    public static bool IsExternalAssemblyAllowed(string simpleName) =>
        StrategyBundleExternalAssemblyPolicy.IsAllowed(simpleName);

    [DoesNotReturn]
    private static void Reject(string message) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            message);
}
