using System.Text.Json.Serialization;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>One downloadable build of a plugin in the marketplace index.</summary>
public sealed record PluginFeedVersion(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sdkVersion")] string SdkVersion,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("minAppVersion")] string? MinAppVersion = null,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes = 0,
    [property: JsonPropertyName("signatureThumbprint")] string? SignatureThumbprint = null);

/// <summary>A plugin listed in the feed — its metadata, latest build, and history.</summary>
public sealed record PluginFeedEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("publisher")] string Publisher,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("latest")] PluginFeedVersion Latest,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("paperUrl")] string? PaperUrl = null,
    [property: JsonPropertyName("versions")] IReadOnlyList<PluginFeedVersion>? Versions = null);

/// <summary>A withdrawn build in the feed — synced into the local <c>revoked.json</c> kill-list.</summary>
public sealed record PluginFeedRevocation(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("thumbprint")] string? Thumbprint = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("dateUtc")] string? DateUtc = null);

/// <summary>
/// The marketplace index (<c>plugins-index.json</c>) — a static, versioned, signed list of installable
/// plugins plus a revocation list. Hosted on GitHub Pages with a detached signature; the app fetches it
/// in the background, verifies it against the pinned key (<see cref="FeedSignatureVerifier"/>), and never
/// blocks startup on it. A later website reads the exact same file — one source of truth.
/// </summary>
public sealed record PluginIndex(
    [property: JsonPropertyName("feedVersion")] int FeedVersion,
    [property: JsonPropertyName("plugins")] IReadOnlyList<PluginFeedEntry> Plugins,
    [property: JsonPropertyName("publishedUtc")] string? PublishedUtc = null,
    [property: JsonPropertyName("revoked")] IReadOnlyList<PluginFeedRevocation>? Revoked = null)
{
    /// <summary>The schema version this app understands. A higher <see cref="FeedVersion"/> is treated as
    /// "newer than me" and ignored rather than mis-parsed.</summary>
    public const int SupportedFeedVersion = 1;
}
