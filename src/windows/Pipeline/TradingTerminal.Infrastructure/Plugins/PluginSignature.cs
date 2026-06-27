namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>The result of inspecting a plugin assembly's code signature.</summary>
/// <param name="IsSigned">True when the file carries an Authenticode signer certificate.</param>
/// <param name="IsValid">True when the embedded signature is cryptographically valid (file untampered,
/// signature verifies). False for unsigned or tampered files.</param>
/// <param name="Thumbprint">The signer certificate thumbprint (the value pinned by the trust policy),
/// or null when unsigned.</param>
/// <param name="Subject">The signer certificate subject, for diagnostics.</param>
public sealed record PluginSignature(bool IsSigned, bool IsValid, string? Thumbprint, string? Subject)
{
    /// <summary>An unsigned assembly.</summary>
    public static PluginSignature Unsigned { get; } = new(false, false, null, null);
}

/// <summary>
/// Inspects a plugin assembly's code signature. A seam so the trust-policy decision is unit-testable
/// without real signed binaries — a fake inspector returns a chosen <see cref="PluginSignature"/>.
/// The production implementation is <see cref="AuthenticodeSignatureInspector"/>.
/// </summary>
public interface IPluginSignatureInspector
{
    PluginSignature Inspect(string assemblyPath);
}

/// <summary>Always reports unsigned. Used off-Windows and as a safe default where Authenticode is
/// unavailable; combined with a require-signature policy it rejects everything.</summary>
public sealed class NullSignatureInspector : IPluginSignatureInspector
{
    public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
}
