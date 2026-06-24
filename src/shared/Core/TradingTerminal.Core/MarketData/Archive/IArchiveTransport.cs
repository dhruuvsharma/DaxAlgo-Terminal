namespace TradingTerminal.Core.MarketData.Archive;

/// <summary>
/// Abstraction over wherever archived bundles get sent. The default impl talks Telegram MTProto,
/// but the interface is intentionally narrow — any blob-store backend (S3, Dropbox, a folder on
/// another drive) can satisfy it. The archiver computes the bundle and sha256s; the transport
/// only ships bytes.
/// </summary>
public interface IArchiveTransport
{
    /// <summary>Human-readable name of this transport, persisted into manifests so the right
    /// transport is picked at restore time.</summary>
    string Name { get; }

    /// <summary>True when the transport has its credentials and can ship right now.</summary>
    bool IsReady { get; }

    /// <summary>Upload one file. <paramref name="displayName"/> is the user-visible filename
    /// shown in Telegram / the destination. Returns an opaque reference the transport can use
    /// later to download the same bytes.</summary>
    Task<ArchiveBlobRef> UploadAsync(
        Stream content,
        string displayName,
        long contentLength,
        ArchiveTarget target,
        IProgress<long>? progress,
        CancellationToken ct);

    /// <summary>Download one part by reference into <paramref name="destination"/>.</summary>
    Task DownloadAsync(
        ArchiveBlobRef blob,
        Stream destination,
        IProgress<long>? progress,
        CancellationToken ct);
}
