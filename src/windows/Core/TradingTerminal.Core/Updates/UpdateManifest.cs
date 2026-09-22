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
    /// Where the installer can be downloaded. Consumed by <c>IUpdateDownloader</c>, which requires
    /// absolute https, verifies <see cref="Sha256"/> over the downloaded bytes, and checks the
    /// installer's own Authenticode signature before anything is executed. A manifest signature
    /// proves the manifest is ours; it does not make an arbitrary download safe to execute, which is
    /// why all three checks stand on top of it.
    ///
    /// <para>Empty means this release is announce-only: the banner still links to the release notes
    /// and the user installs by hand.</para>
    /// </summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    /// <summary>
    /// Lower-case hex SHA-256 of the installer at <see cref="DownloadUrl"/>. Required whenever
    /// <see cref="DownloadUrl"/> is set — a download with nothing to check it against is not offered
    /// for installation, because the signature covers this manifest and not the bytes it points at.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>
    /// Expected size of the installer in bytes, or 0 when the feed did not say. Advisory only: it
    /// gives the progress bar a total before the first byte arrives and lets an obviously wrong
    /// response be abandoned early. The real ceiling is <c>UpdatesOptions.MaxInstallerBytes</c>,
    /// enforced against the bytes actually received rather than against anything the server claims.
    /// </summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }
}
