using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Keeps an authored artifact so the unit is still there after a restart.
///
/// <para>A seam rather than a direct call because the view-model has no business holding a trust policy
/// or a signature inspector, and because not every edition keeps units this way — Pro's units are meant
/// to arrive as sealed artifacts from the server compiler, so it composes none of this and the composer
/// simply says the unit will not persist.</para>
/// </summary>
public interface IAuthoredUnitStore
{
    /// <summary>Installs <paramref name="artifactPath"/> into <paramref name="root"/>. Never throws.</summary>
    PluginInstallResult Install(string artifactPath, string root);
}

/// <summary>
/// Installs authored artifacts through the ordinary plugin installer, under the host's configured trust
/// policy.
///
/// <para><b>The policy is not relaxed for locally-authored code.</b> It would be easy to argue for it —
/// the user wrote this, on this machine, and reviewed it — but a host configured to run only
/// pinned-publisher code was configured that way by someone, and "except for the strategies the AI
/// wrote" is the one exception that would matter most to whoever wanted around it. A Curated host
/// refuses an unsigned local build and the composer says so plainly.</para>
///
/// <para>The scan is <see cref="PluginScanMode.Enforce"/> and the load side uses the sandbox profile,
/// which is stricter than the curated one and cannot be relaxed by a manifest.</para>
/// </summary>
public sealed class AuthoredUnitStore(PluginHostContext host, IPluginSignatureInspector? inspector = null)
    : IAuthoredUnitStore
{
    public PluginInstallResult Install(string artifactPath, string root)
    {
        if (string.IsNullOrWhiteSpace(artifactPath)) return new(false, "No artifact to install.");
        if (string.IsNullOrWhiteSpace(root)) return new(false, "No units folder to install into.");

        return PluginInstaller.InstallFromArtifact(
            artifactPath,
            root,
            host.TrustPolicy,
            inspector ?? new AuthenticodeSignatureInspector(),
            host.State,
            PluginScanMode.Enforce);
    }
}
