namespace TradingTerminal.Core.Ml;

/// <summary>
/// The registry coordinate a model is filed under. A trained model is scoped to one instrument, one
/// timeframe, one model family and one algorithm; successive checkpoints of that same coordinate are
/// versions. This is the key both producers (a chart window, the future ML Lab) and consumers (a
/// chart overlay, a strategy signal) look models up by.
/// </summary>
public sealed record ModelKey(string ModelKind, string InstrumentKey, string Timeframe, string Algorithm);

/// <summary>What the registry hands back after a successful <see cref="IModelRegistry.Save"/>: the
/// assigned unique id, the version it was filed as, its content digest, and when it was stored.</summary>
public sealed record StoredModel(string ModelId, int Version, string Sha256, DateTime CreatedUtc);

/// <summary>A lightweight listing row — everything needed to browse/pick a model without deserializing
/// its weights: its id and key, version, training size, the model-card metrics, digest and age.</summary>
public sealed record StoredModelInfo(
    string ModelId,
    ModelKey Key,
    int Version,
    long SamplesTrained,
    ModelMetrics Metrics,
    string Sha256,
    DateTime CreatedUtc);

/// <summary>
/// Local, versioned store of trained model artifacts — the keystone that makes a model outlive the
/// window that produced it and be reusable elsewhere. Cloned in spirit from the archive-manifest /
/// repro-job stores: small, purely local SQLite, denormalized metadata for cheap listing, a content
/// digest per row, and soft-delete retention. It holds ONLY model weights + metadata — never market
/// data, never credentials.
///
/// <para>Implementations must be safe to share as a singleton across the app (one writer, concurrent
/// readers).</para>
/// </summary>
public interface IModelRegistry
{
    /// <summary>Files a new checkpoint. The registry assigns the next version for the artifact's
    /// <see cref="ModelArtifact.Key"/>, a unique id, and a content digest, and returns them.</summary>
    StoredModel Save(ModelArtifact artifact);

    /// <summary>Loads a specific model by its unique id, or null if unknown/deleted.</summary>
    ModelArtifact? Load(string modelId);

    /// <summary>Loads the newest (highest-version) non-deleted model for a coordinate, or null when
    /// none has been trained yet — the "warm-resume this window from its last checkpoint" path.</summary>
    ModelArtifact? LoadLatest(ModelKey key);

    /// <summary>Browses stored models, most recent first, optionally filtered. A null field in
    /// <paramref name="filter"/> is a wildcard; a null filter lists everything.</summary>
    IReadOnlyList<StoredModelInfo> List(ModelKey? filter, int maxRows);

    /// <summary>Soft-deletes one model by id. Returns false when it was already absent.</summary>
    bool Delete(string modelId);

    /// <summary>Soft-deletes models older than the retention window. 0 or negative → no-op.</summary>
    int PruneOlderThan(int retentionDays);
}
