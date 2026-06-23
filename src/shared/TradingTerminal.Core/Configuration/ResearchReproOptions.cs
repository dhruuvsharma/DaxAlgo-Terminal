using TradingTerminal.Core.Research;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Knobs for the Paper Lab reproduction subsystem. Bound from the <c>ResearchRepro</c> section of
/// appsettings. Everything defaults so the app builds/runs with NO sidecar and NO Docker — the seams
/// degrade gracefully (Null ingest client, runner reports unavailable), same shape as the AI analyst.
/// </summary>
public sealed class ResearchReproOptions
{
    public const string SectionName = "ResearchRepro";

    /// <summary>Master switch. When false the ingest client is Null and no jobs run.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Base URL of the Python sidecar's paper-resolution endpoints. MUST be a <c>127.0.0.1</c> /
    /// <c>localhost</c> loopback address — the sidecar binds loopback only. Empty → ingest stays Null
    /// even when <see cref="Enabled"/> is true.
    /// </summary>
    public string SidecarBaseUrl { get; set; } = "";

    /// <summary>Per-call timeout for the sidecar HTTP calls.</summary>
    public int SidecarTimeoutSeconds { get; set; } = 60;

    /// <summary>Which sandbox backend to run reproductions in.</summary>
    public SandboxKind SandboxKind { get; set; } = SandboxKind.Docker;

    /// <summary>How long completed job rows are retained before soft-delete. 0 → keep forever.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Path to the reproduction job SQLite file. Falls back to
    /// %LocalAppData%/DaxAlgoTerminal/repro-jobs.db when null/blank.</summary>
    public string? JobDatabasePath { get; set; }
}
