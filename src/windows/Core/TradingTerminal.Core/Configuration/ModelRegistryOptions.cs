namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the local trained-model registry (the SQLite store behind <c>IModelRegistry</c>).
/// Small and purely local, mirroring the repro-job store: its own DB file under the app data folder,
/// independent of the market-data backend.
/// </summary>
public sealed class ModelRegistryOptions
{
    public const string SectionName = "ModelRegistry";

    /// <summary>SQLite database file path. Empty → a default (<c>ml-models.db</c>) under the app's
    /// local data folder.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>Soft-delete models whose checkpoint is older than this many days, pruned on startup.
    /// 0 or negative (default) → keep forever; model artifacts are tiny and their value compounds.</summary>
    public int RetentionDays { get; set; } = 0;
}
