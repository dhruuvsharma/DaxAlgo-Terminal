using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

internal interface IGoogleIdTokenSource
{
    string? GetIdToken(AccountSessionSnapshot session);
}

internal interface IPlatformAccountIdentityBinder
{
    bool TryBindPlatformAccount(AccountSessionSnapshot session, string productAccountId);
}

internal interface ICanonicalAccountSessionSource
{
    AccountSessionSnapshot GetCanonicalSession(AccountSessionSnapshot session);
}

internal interface IDeviceIdentityProvider
{
    Guid GetDeviceId();
}

internal sealed class PlatformEntitlementService(
    HttpClient httpClient,
    Uri platformBaseUri,
    IGoogleIdTokenSource idTokenSource,
    IPlatformAccountIdentityBinder accountIdentityBinder,
    IDeviceIdentityProvider deviceIdentityProvider,
    IOfflineLeaseValidator leaseValidator,
    TimeProvider timeProvider,
    TimeSpan requestTimeout) : IEntitlementService
{
    private const int MaximumLeaseResponseBytes = 256 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
        };

    private readonly Uri _leaseEndpoint = new(
        platformBaseUri.AbsoluteUri.TrimEnd('/') + "/api/v1/entitlements/lease",
        UriKind.Absolute);

    public async Task<SubscriptionEntitlement?> GetEntitlementAsync(
        AccountSessionSnapshot session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();

        try
        {
            var idToken = idTokenSource.GetIdToken(session);
            if (string.IsNullOrWhiteSpace(idToken)) return null;

            var deviceId = deviceIdentityProvider.GetDeviceId();
            if (deviceId == Guid.Empty) return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, _leaseEndpoint)
            {
                Content = JsonContent.Create(new IssueLeaseRequest(deviceId), options: SerializerOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaximumLeaseResponseBytes) return null;

            var wireLease = await ReadLeaseAsync(response.Content, timeout.Token);
            if (wireLease is null) return null;

            var validation = await leaseValidator.ValidateAsync(
                wireLease.ToSignedEnvelope(),
                timeout.Token);
            if (!validation.IsValid || validation.Lease is not { } signedLease)
                return null;
            if (!MatchesSignedClaims(wireLease, signedLease)) return null;

            var signedRequest = new EntitlementAccessRequest(
                signedLease.Entitlement.AccountId,
                AppEdition.Basic,
                timeProvider.GetUtcNow());
            var signedDecision = EntitlementAccessEvaluator.EvaluateOffline(
                signedRequest,
                deviceId.ToString("D"),
                validation);
            if (!signedDecision.IsGranted) return null;

            if (!accountIdentityBinder.TryBindPlatformAccount(
                    session,
                    signedLease.Entitlement.AccountId))
            {
                return null;
            }

            return signedLease.Entitlement;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Platform failures are intentionally opaque: bearer tokens and leases are never logged.
            return null;
        }
    }

    private static bool MatchesSignedClaims(
        EntitlementLeaseWireDto wireLease,
        OfflineEntitlementLease signedLease) =>
        string.Equals(wireLease.LeaseId, signedLease.LeaseId, StringComparison.Ordinal)
        && wireLease.State == signedLease.Entitlement.State
        && wireLease.Edition == signedLease.Entitlement.Edition
        && string.Equals(
            wireLease.ProductAccountId,
            signedLease.Entitlement.AccountId,
            StringComparison.Ordinal)
        && wireLease.DeviceId.ToString("D").Equals(
            signedLease.DeviceId,
            StringComparison.Ordinal)
        && wireLease.IssuedAtUtc == signedLease.IssuedAtUtc
        && wireLease.NotBeforeUtc == signedLease.NotBeforeUtc
        && wireLease.ExpiresAtUtc == signedLease.ExpiresAtUtc;

    private static async Task<EntitlementLeaseWireDto?> ReadLeaseAsync(
        HttpContent content,
        CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var bytesRead = await input.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;
                if (body.Length + bytesRead > MaximumLeaseResponseBytes) return null;
                await body.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }

            body.Position = 0;
            return await JsonSerializer.DeserializeAsync<EntitlementLeaseWireDto>(
                body,
                SerializerOptions,
                ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (body.TryGetBuffer(out var bodyBuffer))
            {
                CryptographicOperations.ZeroMemory(
                    bodyBuffer.AsSpan(0, checked((int)body.Length)));
            }
        }
    }

    private sealed record IssueLeaseRequest(
        [property: JsonPropertyName("deviceId")] Guid DeviceId);
}

internal sealed class Es256OfflineLeaseValidator(string publicKeySpki)
    : IOfflineLeaseValidator
{
    private const string ExpectedAlgorithm = "ES256";
    private const string ExpectedIssuer = "daxalgo-platform";
    private const string ExpectedAudience = "daxalgo-desktop";
    private const int MaximumPayloadBytes = 64 * 1024;
    private static readonly HashSet<string> ExpectedClaimNames = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "leaseId",
        "state",
        "edition",
        "productAccountId",
        "deviceId",
        "issuer",
        "audience",
        "issuedAtUtc",
        "notBeforeUtc",
        "expiresAtUtc",
    };
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public Task<OfflineLeaseValidationResult> ValidateAsync(
        SignedOfflineLeaseEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!string.Equals(envelope.Algorithm, ExpectedAlgorithm, StringComparison.Ordinal))
        {
            return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                OfflineLeaseValidationFailure.UnsupportedAlgorithm));
        }

        if (string.IsNullOrWhiteSpace(publicKeySpki))
        {
            return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                OfflineLeaseValidationFailure.UnknownSigningKey));
        }

        byte[]? payload = null;
        byte[]? signature = null;
        byte[]? publicKey = null;
        try
        {
            payload = DecodeBase64Url(envelope.EncodedPayload);
            signature = DecodeBase64Url(envelope.EncodedSignature);
            if (payload is not { Length: > 0 and <= MaximumPayloadBytes } ||
                signature is not { Length: 64 })
            {
                return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                    OfflineLeaseValidationFailure.MalformedEnvelope));
            }

            publicKey = Convert.FromBase64String(publicKeySpki.Trim());
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            var parameters = verifier.ExportParameters(includePrivateParameters: false);
            if (bytesRead != publicKey.Length ||
                !string.Equals(
                    parameters.Curve.Oid.Value,
                    ECCurve.NamedCurves.nistP256.Oid.Value,
                    StringComparison.Ordinal) ||
                parameters.Q.X is not { Length: 32 } ||
                parameters.Q.Y is not { Length: 32 })
            {
                return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                    OfflineLeaseValidationFailure.UnknownSigningKey));
            }

            if (!verifier.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                    OfflineLeaseValidationFailure.InvalidSignature));
            }

            if (!HasExactClaimShape(payload))
            {
                return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                    OfflineLeaseValidationFailure.InvalidPayload));
            }

            var claims = JsonSerializer.Deserialize<SignedLeaseClaims>(payload, SerializerOptions);
            if (claims is null ||
                claims.SchemaVersion != EntitlementLeaseWireDto.CurrentSchemaVersion ||
                claims.DeviceId == Guid.Empty ||
                !string.Equals(claims.Issuer, ExpectedIssuer, StringComparison.Ordinal) ||
                !string.Equals(claims.Audience, ExpectedAudience, StringComparison.Ordinal))
            {
                return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                    OfflineLeaseValidationFailure.InvalidPayload));
            }

            SubscriptionEntitlement entitlement = new(
                claims.ProductAccountId,
                claims.Edition,
                claims.State,
                claims.NotBeforeUtc,
                claims.ExpiresAtUtc,
                subscriptionReference: claims.LeaseId);
            OfflineEntitlementLease lease = new(
                claims.LeaseId,
                entitlement,
                claims.DeviceId.ToString("D"),
                claims.IssuedAtUtc,
                claims.NotBeforeUtc,
                claims.ExpiresAtUtc);
            return Task.FromResult(OfflineLeaseValidationResult.Valid(lease));
        }
        catch (FormatException)
        {
            return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                OfflineLeaseValidationFailure.MalformedEnvelope));
        }
        catch (CryptographicException)
        {
            return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                OfflineLeaseValidationFailure.UnknownSigningKey));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Task.FromResult(OfflineLeaseValidationResult.Invalid(
                OfflineLeaseValidationFailure.InvalidPayload));
        }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (publicKey is not null) CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(
            base64.Length + ((4 - (base64.Length % 4)) % 4),
            '=');
        return Convert.FromBase64String(base64);
    }

    private static bool HasExactClaimShape(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!ExpectedClaimNames.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }

        return seen.Count == ExpectedClaimNames.Count;
    }

    private sealed record SignedLeaseClaims(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("leaseId")] string LeaseId,
        [property: JsonPropertyName("state")] SubscriptionEntitlementState State,
        [property: JsonPropertyName("edition")] AppEdition Edition,
        [property: JsonPropertyName("productAccountId")] string ProductAccountId,
        [property: JsonPropertyName("deviceId")] Guid DeviceId,
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("audience")] string Audience,
        [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
        [property: JsonPropertyName("notBeforeUtc")] DateTimeOffset NotBeforeUtc,
        [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);
}

internal sealed class PersistedDeviceIdentityProvider(string filePath)
    : IDeviceIdentityProvider
{
    private const string DeviceIdentityMutexName =
        @"Local\DaxAlgoTerminal.PlatformDeviceIdentity.v1";
    private static readonly object ProcessGate = new();

    public static PersistedDeviceIdentityProvider CreateDefault()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgoTerminal");
        return new PersistedDeviceIdentityProvider(
            Path.Combine(directory, "platform-device-id"));
    }

    public Guid GetDeviceId()
    {
        lock (ProcessGate)
        {
            using var mutex = new Mutex(initiallyOwned: false, DeviceIdentityMutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                    throw new IOException("Timed out acquiring the platform device identity lock.");

                return ReadOrCreateDeviceId();
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
        }
    }

    private Guid ReadOrCreateDeviceId()
    {
        var readResult = TryRead(out var existing);
        if (readResult == DeviceIdentityReadResult.Valid) return existing;
        if (readResult == DeviceIdentityReadResult.Invalid)
            throw new IOException("The persisted platform device identity is invalid or unreadable.");

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("The platform device identity path has no parent directory.");

        Directory.CreateDirectory(directory);
        var deviceId = Guid.NewGuid();
        var encoded = Encoding.UTF8.GetBytes(deviceId.ToString("D"));
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            stream.Write(encoded);
            stream.Flush(flushToDisk: true);
            return deviceId;
        }
        catch (IOException)
        {
            if (TryRead(out var winner) == DeviceIdentityReadResult.Valid) return winner;
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private DeviceIdentityReadResult TryRead(out Guid deviceId)
    {
        deviceId = Guid.Empty;
        if (!File.Exists(filePath)) return DeviceIdentityReadResult.Missing;

        try
        {
            return Guid.TryParseExact(File.ReadAllText(filePath).Trim(), "D", out deviceId) &&
                   deviceId != Guid.Empty
                ? DeviceIdentityReadResult.Valid
                : DeviceIdentityReadResult.Invalid;
        }
        catch
        {
            return DeviceIdentityReadResult.Invalid;
        }
    }

    private enum DeviceIdentityReadResult
    {
        Missing,
        Valid,
        Invalid,
    }
}

internal static class PlatformEndpoint
{
    public static bool TryCreateBaseUri(string? configuredValue, out Uri? baseUri)
    {
        baseUri = null;
        if (!Uri.TryCreate(configuredValue?.Trim(), UriKind.Absolute, out var candidate) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            !string.Equals(candidate.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return false;
        }

        var usesHttps = string.Equals(
            candidate.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
#if DEBUG
        var usesDevelopmentLoopback =
            string.Equals(
                candidate.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            && candidate.IsLoopback;
#else
        const bool usesDevelopmentLoopback = false;
#endif
        if (!usesHttps && !usesDevelopmentLoopback)
        {
            return false;
        }

        baseUri = candidate;
        return true;
    }
}
