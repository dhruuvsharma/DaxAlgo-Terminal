namespace TradingTerminal.Core.Research;

/// <summary>
/// A single output file produced by a reproduction, identified by its declared name, the sha256 of
/// its bytes, and its size. The artifact is the ONLY data-flow path out of the sandbox: results leave
/// as a declared, validated file read back from the scratch volume — never via the container's stdout.
///
/// <para><see cref="LocalPath"/> is the path the runner persisted the validated bytes to on the host
/// (outside the disposable container staging dir, which is deleted after the run) so the
/// <c>IReproSignalBridge</c> can read it back. It is <c>null</c> for results that carry only metadata
/// (e.g. a cached job row, a stub), in which case the bridge yields an empty manifest.</para>
/// </summary>
public sealed record ReproArtifact(string Name, string Sha256Hex, long SizeBytes, string? LocalPath = null);
