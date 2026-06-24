using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// HTTP client for the Python sidecar's paper-resolution endpoint (<c>/research/resolve</c>). Mirrors
/// <c>HttpAiAnalystClient</c>: named <see cref="HttpClient"/>, snake_case JSON, a per-call timeout, and
/// the "never throw" contract — every failure folds into <see cref="PaperIngestResult.Empty"/>.
///
/// <para>The sidecar binds <c>127.0.0.1</c> only; this client validates that the configured base URL is
/// a loopback address before issuing any request.</para>
/// </summary>
internal sealed class HttpPaperIngestClient : IPaperIngestClient
{
    public const string HttpClientName = "paper-ingest";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ResearchReproOptions> _options;
    private readonly ILogger<HttpPaperIngestClient> _logger;

    public HttpPaperIngestClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ResearchReproOptions> options,
        ILogger<HttpPaperIngestClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            var o = _options.CurrentValue;
            return o.Enabled && !string.IsNullOrWhiteSpace(o.SidecarBaseUrl);
        }
    }

    public async Task<PaperIngestResult> ResolveAsync(string url, CancellationToken ct = default)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled) return PaperIngestResult.Empty("Paper ingestion disabled in settings.");
        if (string.IsNullOrWhiteSpace(o.SidecarBaseUrl))
            return PaperIngestResult.Empty("Paper ingestion sidecar URL is empty.");
        if (!IsLoopback(o.SidecarBaseUrl))
            return PaperIngestResult.Empty("Paper ingestion sidecar must bind 127.0.0.1 (loopback only).");
        if (string.IsNullOrWhiteSpace(url))
            return PaperIngestResult.Empty("Paper URL is empty.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.SidecarTimeoutSeconds)));

            var http = _httpFactory.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(o.SidecarBaseUrl.TrimEnd('/') + "/");

            using var response = await http
                .PostAsJsonAsync("research/resolve", new HttpResolveRequest(url), JsonOptions, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Paper resolve HTTP {Status} — returning empty", (int)response.StatusCode);
                return PaperIngestResult.Empty($"Sidecar returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content
                .ReadFromJsonAsync<HttpResolveResponse>(JsonOptions, cts.Token)
                .ConfigureAwait(false);
            if (body is null) return PaperIngestResult.Empty("Sidecar returned empty body.");

            return body.ToResult();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Paper resolve timed out after {Sec}s", o.SidecarTimeoutSeconds);
            return PaperIngestResult.Empty($"Paper resolution timed out after {o.SidecarTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Paper resolve sidecar unreachable");
            return PaperIngestResult.Empty("Sidecar unreachable. Is the research endpoint running?");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Paper resolve failed");
            return PaperIngestResult.Empty($"Paper resolution failed: {ex.Message}");
        }
    }

    private static bool IsLoopback(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private sealed record HttpResolveRequest(string Url);

    private sealed record HttpResolveResponse(
        bool Resolved,
        HttpPaper? Paper,
        IReadOnlyList<HttpRepo>? Repos,
        string? Error)
    {
        public PaperIngestResult ToResult()
        {
            if (!Resolved || Paper is null)
                return PaperIngestResult.Empty(Error ?? "Sidecar could not resolve the paper.");

            var repos = (Repos ?? Array.Empty<HttpRepo>())
                .Where(r => !string.IsNullOrWhiteSpace(r.GitUrl) && !string.IsNullOrWhiteSpace(r.Commit))
                .Select(r => new RepoRef(r.GitUrl, r.Commit))
                .ToList();

            return new PaperIngestResult(
                Resolved: true,
                Paper: new PaperRef(Paper.ArxivId ?? string.Empty, Paper.Title ?? string.Empty, Paper.Url ?? string.Empty),
                Repos: repos,
                Error: null);
        }
    }

    private sealed record HttpPaper(string? ArxivId, string? Title, string? Url);
    private sealed record HttpRepo(string GitUrl, string Commit);
}
