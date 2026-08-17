using System;
using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Updates;

/// <summary>
/// The signed release manifest served by the update feed. This is a WIRE CONTRACT — the signature is
/// verified byte-exact over the raw response, so fields may be added but never renamed or reordered
/// in a way that changes meaning, and the app must tolerate unknown fields.
/// </summary>
public sealed record UpdateManifest
{
    /// <summary>Highest schema this app understands. A newer manifest is ignored, not guessed at.</summary>
    public const int SupportedSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The released version, e.g. <c>1.2.0</c>. Parsed with <see cref="System.Version"/>;
    /// semver pre-release suffixes are NOT supported and such a manifest is ignored.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("publishedUtc")]
    public DateTimeOffset? PublishedUtc { get; init; }

    /// <summary>Human-readable release notes. This is what the prompt sends the user to.</summary>
    [JsonPropertyName("releaseNotesUrl")]
    public string ReleaseNotesUrl { get; init; } = string.Empty;

    /// <summary>
    /// Where the installer can be downloaded. **Nothing in the app consumes this yet, by design** —
    /// see <c>IUpdateChecker</c>. Before anything downloads it: require https, verify
    /// <see cref="Sha256"/> over the downloaded bytes, and check the installer's own Authenticode
    /// signature. A manifest signature proves the manifest is ours; it does not make an arbitrary
    /// download safe to execute.
    /// </summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the installer at <see cref="DownloadUrl"/>. Unused today.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}
