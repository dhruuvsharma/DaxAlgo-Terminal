using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Reference;

/// <summary>
/// Finds reference pictures and pages through the Brave Search API.
///
/// <para><b>Everything about this is bounded, and the reason is not politeness.</b> The bytes it
/// downloads are base64-encoded into a model request on the user's metered key, and the text it
/// gathers reaches the prompt of an agent whose output is compiled and run. So: a result cap, a
/// per-image byte cap, a whole-harvest timeout, a content-type allowlist, and no redirect chasing.</para>
///
/// <para>It never throws. A search that fails costs the bar, not the build — the critics fall back to
/// the rubric the planner wrote, which is weaker but real.</para>
/// </summary>
public sealed class BraveReferenceSearch(
    HttpClient http,
    ReferenceSearchOptions options,
    ILogger? logger = null) : IReferenceSearch
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ReferenceSearchOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Brave's image endpoint.</summary>
    public const string ImageEndpoint = "https://api.search.brave.com/res/v1/images/search";

    /// <summary>Brave's web endpoint, used when pictures are not what a brief needs.</summary>
    public const string WebEndpoint = "https://api.search.brave.com/res/v1/web/search";

    /// <summary>
    /// The only image types accepted.
    ///
    /// <para>An allowlist rather than a blocklist, and it is checked against the RESPONSE's declared
    /// type rather than the URL's extension: a URL ending .png that serves HTML is a thing that
    /// happens, and every byte here is going into a model request.</para>
    /// </summary>
    public static IReadOnlyList<string> AcceptedImageTypes { get; } = ["image/png", "image/jpeg", "image/webp"];

    public bool IsConfigured => _options.IsConfigured;

    public async Task<IReadOnlyList<ReferenceCandidate>> FindAsync(
        string query, bool wantImages = true, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return [];

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds)));

        try
        {
            var take = Math.Clamp(_options.Results, 1, 20);
            var endpoint = wantImages ? ImageEndpoint : WebEndpoint;
            var url = $"{endpoint}?q={Uri.EscapeDataString(Trim(query)!)}&count={take}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("X-Subscription-Token", _options.BraveApiKey);

            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogInformation(
                    "Brave search returned {Status}; the critics will use the rubric instead.",
                    (int)response.StatusCode);
                return [];
            }

            var payload = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            var hits = Parse(payload, wantImages).Take(take).ToArray();

            if (!wantImages) return hits;

            // Download in sequence rather than in parallel. Ten concurrent fetches against ten
            // unrelated hosts is a burst nobody asked for, and the whole harvest is already bounded by
            // one deadline — so the cost of doing it politely is measured in seconds, once.
            var gathered = new List<ReferenceCandidate>(hits.Length);
            foreach (var hit in hits)
            {
                if (deadline.IsCancellationRequested) break;
                gathered.Add(hit with { Image = await FetchAsync(hit, deadline.Token).ConfigureAwait(false) });
            }

            return gathered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Including the harvest deadline firing. A slow search must not fail a build.
            logger?.LogInformation(ex, "Reference search failed; the critics will use the rubric instead.");
            return [];
        }
    }

    /// <summary>
    /// Fetches one picture, or returns null.
    ///
    /// <para>Bounded twice over: the declared content type must be one we accept, and the body must be
    /// under the cap. The length is checked from the header first and then again while reading, because
    /// a Content-Length is a claim rather than a fact.</para>
    /// </summary>
    private async Task<CodegenImage?> FetchAsync(ReferenceCandidate hit, CancellationToken ct)
    {
        if (!Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is null || !AcceptedImageTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) return null;

            var cap = Math.Max(64_000, _options.MaximumImageBytes);
            if (response.Content.Headers.ContentLength is { } declared && declared > cap) return null;

            using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();

            var chunk = new byte[81_920];
            int read;
            while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > cap) return null;
                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0
                ? null
                : new CodegenImage(type, buffer.ToArray(), $"REFERENCE: {Trim(hit.Title, 120)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // One reference that would not download is not worth reporting: nine others may have.
            return null;
        }
    }

    /// <summary>Reads whichever of the two shapes came back.</summary>
    private static IEnumerable<ReferenceCandidate> Parse(string payload, bool images)
    {
        var wire = JsonSerializer.Deserialize<BraveResponse>(payload, Json);

        var rows = images
            ? wire?.Results?.Select(r => (r.Title, Url: r.Properties?.Url ?? r.Thumbnail?.Src, r.Description))
            : wire?.Web?.Results?.Select(r => (r.Title, Url: r.Url, r.Description));

        foreach (var row in rows ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Url)) continue;
            yield return new ReferenceCandidate(
                Trim(row.Title ?? "reference", 200)!, row.Url!, Trim(row.Description, 400), null);
        }
    }

    /// <summary>Caps a string from the open web before it reaches a prompt.</summary>
    private static string? Trim(string? value, int max = 300) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record BraveResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<ImageResult>? Results,
        [property: JsonPropertyName("web")] WebSection? Web);

    private sealed record ImageResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("properties")] ImageProperties? Properties,
        [property: JsonPropertyName("thumbnail")] Thumbnail? Thumbnail);

    private sealed record ImageProperties([property: JsonPropertyName("url")] string? Url);

    private sealed record Thumbnail([property: JsonPropertyName("src")] string? Src);

    private sealed record WebSection([property: JsonPropertyName("results")] IReadOnlyList<WebResult>? Results);

    private sealed record WebResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("description")] string? Description);
}
