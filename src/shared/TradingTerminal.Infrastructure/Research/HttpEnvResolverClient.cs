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
/// HTTP client for the Python sidecar's environment-resolution endpoint (<c>/research/plan</c>).
/// Mirrors <see cref="HttpPaperIngestClient"/>: named <see cref="HttpClient"/>, snake_case JSON, a
/// per-call timeout, and the "never throw" contract — every failure folds into
/// <see cref="MinimalReproPlan.Empty"/>.
///
/// <para>The sidecar binds <c>127.0.0.1</c> only and performs STATIC analysis only (it clones + reads
/// the repo's manifest files, never runs the repo's code). This client validates that the configured
/// base URL is a loopback address before issuing any request.</para>
/// </summary>
internal sealed class HttpEnvResolverClient : IEnvResolverClient
{
    public const string HttpClientName = "env-resolver";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<ResearchReproOptions> _options;
    private readonly ILogger<HttpEnvResolverClient> _logger;

    public HttpEnvResolverClient(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<ResearchReproOptions> options,
        ILogger<HttpEnvResolverClient> logger)
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

    public async Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default)
    {
        var o = _options.CurrentValue;
        if (!o.Enabled) return MinimalReproPlan.Empty("Paper research disabled in settings.");
        if (string.IsNullOrWhiteSpace(o.SidecarBaseUrl))
            return MinimalReproPlan.Empty("Research sidecar URL is empty.");
        if (!IsLoopback(o.SidecarBaseUrl))
            return MinimalReproPlan.Empty("Research sidecar must bind 127.0.0.1 (loopback only).");
        if (repo is null || string.IsNullOrWhiteSpace(repo.GitUrl))
            return MinimalReproPlan.Empty("Repo git URL is empty.");
        if (string.IsNullOrWhiteSpace(repo.Commit))
            return MinimalReproPlan.Empty("Repo commit pin is empty (required for determinism).");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.SidecarTimeoutSeconds)));

            var http = _httpFactory.CreateClient(HttpClientName);
            http.BaseAddress = new Uri(o.SidecarBaseUrl.TrimEnd('/') + "/");

            using var response = await http
                .PostAsJsonAsync("research/plan", new HttpPlanRequest(repo.GitUrl, repo.Commit), JsonOptions, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Env resolve HTTP {Status} — returning empty", (int)response.StatusCode);
                return MinimalReproPlan.Empty($"Sidecar returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content
                .ReadFromJsonAsync<HttpPlanResponse>(JsonOptions, cts.Token)
                .ConfigureAwait(false);
            if (body is null) return MinimalReproPlan.Empty("Sidecar returned empty body.");

            return body.ToPlan();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Env resolve timed out after {Sec}s", o.SidecarTimeoutSeconds);
            return MinimalReproPlan.Empty($"Environment resolution timed out after {o.SidecarTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Env resolve sidecar unreachable");
            return MinimalReproPlan.Empty("Sidecar unreachable. Is the research endpoint running?");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Env resolve failed");
            return MinimalReproPlan.Empty($"Environment resolution failed: {ex.Message}");
        }
    }

    private static bool IsLoopback(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private sealed record HttpPlanRequest(string GitUrl, string Commit);

    private sealed record HttpPlanResponse(
        string? Image,
        IReadOnlyList<string>? SetupCommands,
        string? Entrypoint,
        IReadOnlyList<string>? DeclaredDataDeps,
        string? EnvHash,
        string? Error)
    {
        public MinimalReproPlan ToPlan()
        {
            if (!string.IsNullOrWhiteSpace(Error))
                return MinimalReproPlan.Empty(Error!);
            if (string.IsNullOrWhiteSpace(Image) || string.IsNullOrWhiteSpace(Entrypoint))
                return MinimalReproPlan.Empty("Sidecar returned no runnable image/entrypoint.");

            var plan = new EnvResolutionPlan(
                Image: Image!,
                SetupCommands: SetupCommands ?? Array.Empty<string>(),
                Entrypoint: Entrypoint!,
                DeclaredDataDeps: DeclaredDataDeps ?? Array.Empty<string>(),
                EnvHash: new EnvHash(EnvHash ?? string.Empty));

            return MinimalReproPlan.Ok(plan);
        }
    }
}
