namespace TradingTerminal.Core.Research;

/// <summary>
/// Persistence for reproduction jobs. Cloned in shape from the archive manifest store: SQLite, small,
/// purely local. It is what makes jobs survive an app restart and what backs the reproduction cache.
///
/// <para>This store holds ONLY job/result metadata + sha256 artifact refs — never the untrusted paper
/// code and never the canonical market-data store. It must not reach broker credentials or the
/// market-data DB.</para>
/// </summary>
public interface IReproJobStore
{
    /// <summary>Insert or update a job (keyed by <see cref="ReproJob.Id"/>).</summary>
    void Save(ReproJob job);

    /// <summary>Look up a job by id, or null.</summary>
    ReproJob? Find(Guid id);

    /// <summary>
    /// Return a prior <see cref="ReproStatus.Succeeded"/> job whose spec has the given cache key, or
    /// null. This is the cache hit that lets an identical spec skip running a new container.
    /// </summary>
    ReproJob? FindCached(string cacheKey);

    /// <summary>Jobs in a non-terminal state — requeued by the orchestrator on startup.</summary>
    IReadOnlyList<ReproJob> LoadUnfinished();

    /// <summary>All non-soft-deleted jobs, most recent first, capped at <paramref name="maxRows"/>.</summary>
    IReadOnlyList<ReproJob> List(int maxRows);

    /// <summary>Soft-delete jobs older than the retention window. 0 days → no-op.</summary>
    int PruneOlderThan(int retentionDays);
}
