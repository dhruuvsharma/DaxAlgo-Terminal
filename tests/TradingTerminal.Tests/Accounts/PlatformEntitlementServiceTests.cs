using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class PlatformEntitlementServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    [Fact]
    public async Task Signed_platform_lease_grants_active_professional_access()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var lease = CreateLease(signingKey, deviceId);
        var handler = new PlatformHandler(HttpStatusCode.OK, lease);
        using var httpClient = new HttpClient(handler);
        var services = AccountGateServiceFactory.Create(
            "Production",
            timeProvider: new FixedTimeProvider(Now),
            isDebugBuild: false,
            googleIdentityProvider: new StubGoogleIdentityProvider(),
            platformOptions: PlatformConfiguration(signingKey),
            platformHttpClient: httpClient,
            deviceIdentityProvider: new FixedDeviceIdentityProvider(deviceId));
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            new FixedTimeProvider(Now));

        var attempt = await coordinator.AcquireAccessAsync();

        services.Mode.Should().Be(AccountGateProviderMode.Production);
        services.Entitlements.Should().BeOfType<PlatformEntitlementService>();
        attempt.IsGranted.Should().BeTrue();
        attempt.Decision!.GrantedEdition.Should().Be(AppEdition.Professional);
        attempt.Session!.Account.AccountId.Should().Be(
            "11111111-1111-1111-1111-111111111111");
        handler.RequestUri.Should().Be(
            new Uri("https://platform.example/api/v1/entitlements/lease"));
        handler.BearerToken.Should().Be("google-id-token");
        using var requestBody = JsonDocument.Parse(handler.RequestBody!);
        requestBody.RootElement.GetProperty("deviceId").GetGuid().Should().Be(deviceId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Platform_denial_returns_no_entitlement(HttpStatusCode statusCode)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service = CreateService(
            signingKey,
            Guid.NewGuid(),
            new PlatformHandler(statusCode));

        var entitlement = await service.GetEntitlementAsync(Session());

        entitlement.Should().BeNull();
    }

    [Fact]
    public async Task Lease_signed_by_an_untrusted_key_is_rejected()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var untrustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var deviceId = Guid.NewGuid();
        var service = CreateService(
            trustedKey,
            deviceId,
            new PlatformHandler(
                HttpStatusCode.OK,
                CreateLease(untrustedKey, deviceId)));

        var entitlement = await service.GetEntitlementAsync(Session());

        entitlement.Should().BeNull();
    }

    [Fact]
    public async Task Development_mode_keeps_the_local_professional_entitlement()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var services = AccountGateServiceFactory.Create(
            "DevLogin",
            timeProvider: new FixedTimeProvider(Now),
            isDebugBuild: true,
            platformOptions: PlatformConfiguration(signingKey));
        var session = new AccountSessionSnapshot(
            "development-session",
            new AccountIdentity("development-account"),
            Now,
            Now.AddHours(1));

        var entitlement = await services.Entitlements.GetEntitlementAsync(session);

        services.Mode.Should().Be(AccountGateProviderMode.Development);
        services.Entitlements.Should().BeOfType<DevelopmentEntitlementService>();
        entitlement.Should().NotBeNull();
        entitlement!.State.Should().Be(SubscriptionEntitlementState.Active);
        entitlement.Edition.Should().Be(AppEdition.Professional);
    }

    private static PlatformEntitlementService CreateService(
        ECDsa trustedKey,
        Guid deviceId,
        PlatformHandler handler)
    {
        var sessionContext = new FixedPlatformAccountSessionContext();
        return new PlatformEntitlementService(
            new HttpClient(handler),
            new Uri("https://platform.example/"),
            sessionContext,
            sessionContext,
            new FixedDeviceIdentityProvider(deviceId),
            new Es256OfflineLeaseValidator(
                Convert.ToBase64String(trustedKey.ExportSubjectPublicKeyInfo())),
            new FixedTimeProvider(Now),
            TimeSpan.FromSeconds(5));
    }

    private static PlatformOptions PlatformConfiguration(ECDsa signingKey) => new()
    {
        BaseUrl = "https://platform.example",
        EntitlementLeasePublicKey = Convert.ToBase64String(
            signingKey.ExportSubjectPublicKeyInfo()),
        TimeoutSeconds = 5,
    };

    private static AccountSessionSnapshot Session() => new(
        "google-session",
        new AccountIdentity("google:subject-42", "Example Person", "person@example.com"),
        Now.AddMinutes(-1),
        Now.AddHours(1));

    private static EntitlementLeaseWireDto CreateLease(
        ECDsa signingKey,
        Guid deviceId)
    {
        var claims = new SignedLeaseClaims(
            EntitlementLeaseWireDto.CurrentSchemaVersion,
            "lease-42",
            SubscriptionEntitlementState.Active,
            AppEdition.Professional,
            "11111111-1111-1111-1111-111111111111",
            deviceId,
            "daxalgo-platform",
            "daxalgo-desktop",
            Now.AddMinutes(-1),
            Now.AddMinutes(-1),
            Now.AddDays(1));
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, SerializerOptions);
        var signature = signingKey.SignData(
            payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new EntitlementLeaseWireDto(
            claims.SchemaVersion,
            claims.LeaseId,
            claims.State,
            claims.Edition,
            claims.ProductAccountId,
            claims.DeviceId,
            claims.Issuer,
            claims.Audience,
            claims.IssuedAtUtc,
            claims.NotBeforeUtc,
            claims.ExpiresAtUtc,
            "test-edition-lease-key",
            "ES256",
            Base64UrlEncode(payload),
            Base64UrlEncode(signature));
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record SignedLeaseClaims(
        int SchemaVersion,
        string LeaseId,
        SubscriptionEntitlementState State,
        AppEdition Edition,
        string ProductAccountId,
        Guid DeviceId,
        string Issuer,
        string Audience,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset NotBeforeUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedDeviceIdentityProvider(Guid deviceId) : IDeviceIdentityProvider
    {
        public Guid GetDeviceId() => deviceId;
    }

    private sealed class FixedPlatformAccountSessionContext
        : IGoogleIdTokenSource, IPlatformAccountIdentityBinder
    {
        public string? GetIdToken(AccountSessionSnapshot session) => "google-id-token";

        public bool TryBindPlatformAccount(
            AccountSessionSnapshot session,
            string productAccountId) => true;
    }

    private sealed class StubGoogleIdentityProvider : IGoogleIdentityProvider
    {
        public Task<GoogleIdentity> AuthenticateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new GoogleIdentity(
                "subject-42",
                "person@example.com",
                "Example Person",
                Now.AddHours(1),
                "google-id-token"));
        }
    }

    private sealed class PlatformHandler(
        HttpStatusCode statusCode,
        EntitlementLeaseWireDto? lease = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? BearerToken { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            BearerToken = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode);
            if (lease is not null)
            {
                response.Content = JsonContent.Create(lease, options: SerializerOptions);
            }

            return response;
        }
    }
}
