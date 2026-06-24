using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research.Bridge;

/// <summary>
/// Parses a succeeded reproduction's declared <c>result.json</c> artifact (schema documented on
/// <see cref="ReproSignalManifest"/>) into a canonical, InstrumentId-keyed <see cref="ReproSignalManifest"/>.
/// This is the ONLY path the reproduced output takes into the engine — data/signals only, no live orders.
/// The bridge never executes any of the untrusted code; it only reads the validated artifact bytes the
/// runner persisted to a durable host path.
///
/// <para>Never throws across the seam (same contract as <c>IAiAnalystClient</c>): a failed result,
/// missing/unreadable artifact, malformed JSON, provenance mismatch, or zero usable rows all fold into
/// <see cref="ReproSignalManifest.Empty"/>. Malformed individual signal rows are dropped, not fatal.</para>
/// </summary>
internal sealed class ReproSignalBridge : IReproSignalBridge
{
    /// <summary>The declared artifact the sandbox emits; the only data-flow path out.</summary>
    private const string ArtifactFileName = "result.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<ReproSignalBridge> _logger;

    public ReproSignalBridge(ILogger<ReproSignalBridge> logger) => _logger = logger;

    public async Task<ReproSignalManifest> MapAsync(ReproResult result, CancellationToken ct = default)
    {
        // Provenance we can fold into the empty manifest on any early-out.
        var knownPaper = new PaperRef(result.PaperArxivId, string.Empty, ArxivUrl(result.PaperArxivId));

        try
        {
            if (!result.Success)
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);

            var artifact = result.Artifacts.FirstOrDefault(a =>
                string.Equals(a.Name, ArtifactFileName, StringComparison.OrdinalIgnoreCase));
            if (artifact?.LocalPath is not { Length: > 0 } path || !File.Exists(path))
            {
                _logger.LogDebug("Repro bridge: no readable artifact for {Arxiv}@{Commit}",
                    result.PaperArxivId, result.RepoCommit);
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);
            }

            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

            ArtifactDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ArtifactDto>(bytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Repro bridge: artifact JSON malformed for {Arxiv}", result.PaperArxivId);
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);
            }

            if (dto is null)
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);

            // Provenance guard: the artifact's claimed paper/commit must match the result we ran. A
            // mismatch means the container wrote a result for something other than what we asked for —
            // refuse to trust it. Empty (not partial) so the provenance triple is never forged.
            if (!ProvenanceMatches(dto, result))
            {
                _logger.LogWarning(
                    "Repro bridge: artifact provenance mismatch (artifact {AP}@{AC} vs result {RP}@{RC}); dropping.",
                    dto.PaperId, dto.Commit, result.PaperArxivId, result.RepoCommit);
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);
            }

            var signals = NormalizeSignals(dto, result);
            if (signals.Count == 0)
            {
                _logger.LogDebug("Repro bridge: artifact parsed but no usable signals for {Arxiv}", result.PaperArxivId);
                return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);
            }

            return new ReproSignalManifest(knownPaper, result.RepoCommit, result.EnvHash, signals);
        }
        catch (Exception ex)
        {
            // Last-resort net: never throw across the seam.
            _logger.LogDebug(ex, "Repro bridge: unexpected failure mapping artifact for {Arxiv}", result.PaperArxivId);
            return ReproSignalManifest.Empty(knownPaper, result.RepoCommit, result.EnvHash);
        }
    }

    private static bool ProvenanceMatches(ArtifactDto dto, ReproResult result)
    {
        // Empty fields in the artifact are tolerated (the sidecar may omit them); a NON-empty field that
        // disagrees is a hard mismatch.
        bool paperOk = string.IsNullOrEmpty(dto.PaperId)
            || string.Equals(dto.PaperId, result.PaperArxivId, StringComparison.OrdinalIgnoreCase);
        bool commitOk = string.IsNullOrEmpty(dto.Commit)
            || string.Equals(dto.Commit, result.RepoCommit, StringComparison.OrdinalIgnoreCase);
        return paperOk && commitOk;
    }

    /// <summary>
    /// Map artifact rows to canonical <see cref="ReproducedSignal"/>s: validate each field, drop
    /// malformed rows, stamp the result's provenance triple onto every survivor, and return time-sorted.
    /// </summary>
    private static IReadOnlyList<ReproducedSignal> NormalizeSignals(ArtifactDto dto, ReproResult result)
    {
        var rows = dto.Signals;
        if (rows is null || rows.Count == 0) return Array.Empty<ReproducedSignal>();

        var mapped = new List<ReproducedSignal>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null) continue;

            // instrument_id must resolve to a non-None canonical id.
            if (row.InstrumentId is not { } idValue || idValue <= 0) continue;

            // value must be a finite number.
            if (row.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value)) continue;

            // timestamp must parse to a UTC instant.
            if (!TryParseUtc(row.TimestampUtc, out var ts)) continue;

            var kind = ParseKind(row.Kind);

            mapped.Add(new ReproducedSignal(
                Instrument: new InstrumentId(idValue),
                EventTimeUtc: ts,
                Value: value,
                PaperArxivId: result.PaperArxivId,
                RepoCommit: result.RepoCommit,
                EnvHash: result.EnvHash,
                Kind: kind));
        }

        // Deterministic time order — the replay kernel consumes signals as the clock crosses them.
        mapped.Sort((a, b) => a.EventTimeUtc.CompareTo(b.EventTimeUtc));
        return mapped;
    }

    private static bool TryParseUtc(string? raw, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!DateTime.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            return false;
        utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    private static ReproSignalKind ParseKind(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "score" => ReproSignalKind.Score,
        "weight" => ReproSignalKind.Weight,
        _ => ReproSignalKind.Position,
    };

    private static string ArxivUrl(string arxivId) =>
        string.IsNullOrWhiteSpace(arxivId) ? string.Empty : $"https://arxiv.org/abs/{arxivId}";

    // ── On-disk artifact DTOs (snake_case; see ReproSignalManifest for the full schema) ────────────
    private sealed record ArtifactDto(
        [property: JsonPropertyName("paper_id")] string? PaperId,
        [property: JsonPropertyName("commit")] string? Commit,
        [property: JsonPropertyName("env_hash")] string? EnvHash,
        [property: JsonPropertyName("signals")] List<SignalDto?>? Signals);

    private sealed record SignalDto(
        [property: JsonPropertyName("instrument_id")] int? InstrumentId,
        [property: JsonPropertyName("timestamp_utc")] string? TimestampUtc,
        [property: JsonPropertyName("value")] double? Value,
        [property: JsonPropertyName("kind")] string? Kind);
}
