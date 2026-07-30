using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class GoogleOAuthClientTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authorization_code_pkce_flow_validates_google_identity()
    {
        using var rsa = RSA.Create(2048);
        var browser = new CapturingBrowser();
        var callback = new CallbackReceiver(browser);
        var handler = new GoogleHandler(rsa, browser, Now);
        using var httpClient = new HttpClient(handler);
        var client = new GoogleOAuthClient(
            new GoogleAuthOptions
            {
                ClientId = "desktop-client.apps.googleusercontent.com",
                ClientSecret = "optional-client-secret",
            },
            httpClient,
            new FixedTimeProvider(Now),
            new CallbackReceiverFactory(callback),
            browser);

        var identity = await client.AuthenticateAsync();

        identity.Subject.Should().Be("google-subject-42");
        identity.EmailAddress.Should().Be("person@example.com");
        identity.DisplayName.Should().Be("Example Person");
        identity.TokenExpiresAtUtc.Should().Be(Now.AddHours(1));
        identity.IdToken.Split('.').Should().HaveCount(3);

        browser.AuthorizationUri.Should().NotBeNull();
        browser.AuthorizationUri!.GetLeftPart(UriPartial.Path).Should()
            .Be(GoogleOAuthClient.AuthorizationEndpoint.AbsoluteUri);
        var authorization = ParseQuery(browser.AuthorizationUri.Query);
        authorization["client_id"].Should().Be("desktop-client.apps.googleusercontent.com");
        authorization["redirect_uri"].Should()
            .Be("http://127.0.0.1:54321/oidc/callback/");
        authorization["response_type"].Should().Be("code");
        authorization["scope"].Should().Be("openid email profile");
        authorization["code_challenge_method"].Should().Be("S256");
        authorization["access_type"].Should().Be("offline");
        authorization["prompt"].Should().Be("consent");
        authorization["state"].Should().NotBeNullOrWhiteSpace();
        authorization["nonce"].Should().NotBeNullOrWhiteSpace();

        handler.TokenRequest.Should().NotBeNull();
        handler.TokenRequest!["code"].Should().Be("authorization-code");
        handler.TokenRequest["client_id"].Should()
            .Be("desktop-client.apps.googleusercontent.com");
        handler.TokenRequest["redirect_uri"].Should()
            .Be("http://127.0.0.1:54321/oidc/callback/");
        handler.TokenRequest["grant_type"].Should().Be("authorization_code");
        handler.TokenRequest["client_secret"].Should().Be("optional-client-secret");
        var verifier = handler.TokenRequest["code_verifier"];
        verifier.Length.Should().BeGreaterThanOrEqualTo(43);
        var expectedChallenge = GoogleOAuthClient.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        authorization["code_challenge"].Should().Be(expectedChallenge);
    }

    [Fact]
    public async Task Callback_state_mismatch_is_rejected_before_token_exchange()
    {
        using var rsa = RSA.Create(2048);
        var browser = new CapturingBrowser();
        var callback = new CallbackReceiver(browser, stateOverride: "wrong-state");
        var handler = new GoogleHandler(rsa, browser, Now);
        using var httpClient = new HttpClient(handler);
        var client = new GoogleOAuthClient(
            new GoogleAuthOptions { ClientId = "desktop-client.apps.googleusercontent.com" },
            httpClient,
            new FixedTimeProvider(Now),
            new CallbackReceiverFactory(callback),
            browser);

        var act = () => client.AuthenticateAsync();

        await act.Should().ThrowAsync<AccountProviderUnavailableException>();
        handler.TokenRequest.Should().BeNull();
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("exp")]
    [InlineData("nonce")]
    public async Task Invalid_required_id_token_claim_is_rejected(string invalidClaim)
    {
        using var rsa = RSA.Create(2048);
        var browser = new CapturingBrowser();
        var callback = new CallbackReceiver(browser);
        var handler = new GoogleHandler(rsa, browser, Now, invalidClaim);
        using var httpClient = new HttpClient(handler);
        var client = new GoogleOAuthClient(
            new GoogleAuthOptions { ClientId = "desktop-client.apps.googleusercontent.com" },
            httpClient,
            new FixedTimeProvider(Now),
            new CallbackReceiverFactory(callback),
            browser);

        var act = () => client.AuthenticateAsync();

        await act.Should().ThrowAsync<AccountProviderUnavailableException>();
    }

    [Fact]
    public async Task Id_token_signed_by_an_unknown_key_is_rejected()
    {
        using var signingKey = RSA.Create(2048);
        using var advertisedKey = RSA.Create(2048);
        var browser = new CapturingBrowser();
        var callback = new CallbackReceiver(browser);
        var handler = new GoogleHandler(signingKey, browser, Now, jwksKey: advertisedKey);
        using var httpClient = new HttpClient(handler);
        var client = new GoogleOAuthClient(
            new GoogleAuthOptions { ClientId = "desktop-client.apps.googleusercontent.com" },
            httpClient,
            new FixedTimeProvider(Now),
            new CallbackReceiverFactory(callback),
            browser);

        var act = () => client.AuthenticateAsync();

        await act.Should().ThrowAsync<AccountProviderUnavailableException>();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] =
                parts.Length == 2
                    ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    : string.Empty;
        }

        return values;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingBrowser : IGoogleOAuthBrowser
    {
        public Uri? AuthorizationUri { get; private set; }

        public void Open(Uri authorizationUri) => AuthorizationUri = authorizationUri;
    }

    private sealed class CallbackReceiverFactory(IGoogleOAuthCallbackReceiver receiver)
        : IGoogleOAuthCallbackReceiverFactory
    {
        public IGoogleOAuthCallbackReceiver Create() => receiver;
    }

    private sealed class CallbackReceiver(
        CapturingBrowser browser,
        string? stateOverride = null)
        : IGoogleOAuthCallbackReceiver
    {
        public Uri RedirectUri { get; } =
            new("http://127.0.0.1:54321/oidc/callback/");

        public Task<GoogleOAuthCallback> WaitForCallbackAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var authorization = ParseQuery(browser.AuthorizationUri!.Query);
            return Task.FromResult(new GoogleOAuthCallback(
                "authorization-code",
                stateOverride ?? authorization["state"],
                null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GoogleHandler(
        RSA signingKey,
        CapturingBrowser browser,
        DateTimeOffset now,
        string? invalidClaim = null,
        RSA? jwksKey = null)
        : HttpMessageHandler
    {
        private const string ClientId = "desktop-client.apps.googleusercontent.com";
        private readonly RSA _jwksKey = jwksKey ?? signingKey;

        public Dictionary<string, string>? TokenRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri == GoogleOAuthClient.TokenEndpoint)
            {
                TokenRequest = ParseQuery(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var authorization = ParseQuery(browser.AuthorizationUri!.Query);
                var claims = new Dictionary<string, object?>
                {
                    ["iss"] = invalidClaim == "iss"
                        ? "https://issuer.example.invalid"
                        : GoogleOAuthClient.ExpectedIssuer,
                    ["aud"] = invalidClaim == "aud" ? "another-client" : ClientId,
                    ["exp"] = invalidClaim == "exp"
                        ? now.AddMinutes(-1).ToUnixTimeSeconds()
                        : now.AddHours(1).ToUnixTimeSeconds(),
                    ["nonce"] = invalidClaim == "nonce"
                        ? "different-nonce"
                        : authorization["nonce"],
                    ["sub"] = "google-subject-42",
                    ["email"] = "person@example.com",
                    ["name"] = "Example Person",
                };
                var idToken = CreateJwt(signingKey, claims);
                return JsonResponse(new { id_token = idToken, access_token = "not-persisted" });
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri == GoogleOAuthClient.JwksEndpoint)
            {
                var parameters = _jwksKey.ExportParameters(false);
                return JsonResponse(new
                {
                    keys = new[]
                    {
                        new
                        {
                            kty = "RSA",
                            kid = "test-key",
                            alg = "RS256",
                            use = "sig",
                            n = GoogleOAuthClient.Base64UrlEncode(parameters.Modulus!),
                            e = GoogleOAuthClient.Base64UrlEncode(parameters.Exponent!),
                        },
                    },
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string CreateJwt(
            RSA rsa,
            IReadOnlyDictionary<string, object?> claims)
        {
            var header = GoogleOAuthClient.Base64UrlEncode(
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    alg = "RS256",
                    kid = "test-key",
                    typ = "JWT",
                }));
            var payload = GoogleOAuthClient.Base64UrlEncode(
                JsonSerializer.SerializeToUtf8Bytes(claims));
            var signingInput = Encoding.ASCII.GetBytes(header + "." + payload);
            var signature = rsa.SignData(
                signingInput,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return header + "." + payload + "." +
                   GoogleOAuthClient.Base64UrlEncode(signature);
        }

        private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value)),
        };
    }
}
