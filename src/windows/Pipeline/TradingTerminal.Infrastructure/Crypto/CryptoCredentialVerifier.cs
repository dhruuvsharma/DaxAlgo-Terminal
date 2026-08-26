using System.Net.Http;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// The <see cref="IBrokerCredentialVerifier"/> the shells compose: it runs
/// <see cref="CryptoAccountProbe"/> against the six crypto venues that have a keyed mode.
///
/// <para>One <see cref="HttpClient"/> for the life of the process, with a short timeout. A login window
/// waiting on a verification is a user staring at a spinner, so ten seconds is the whole budget — and a
/// timeout reports <see cref="CredentialVerification.NotChecked"/> rather than a rejection, because a
/// slow network is not a bad key.</para>
/// </summary>
public sealed class CryptoCredentialVerifier : IBrokerCredentialVerifier, IDisposable
{
    private readonly ILogger<CryptoCredentialVerifier> _logger;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;

    public CryptoCredentialVerifier(
        ILogger<CryptoCredentialVerifier> logger, TimeProvider? time = null, HttpClient? http = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public bool CanVerify(BrokerKind broker) => CryptoAccountProbe.Supports(broker);

    public async Task<CredentialVerification> VerifyAsync(
        BrokerKind broker, BrokerCredential credential, CancellationToken ct = default)
    {
        if (!CanVerify(broker)) return CredentialVerification.NotChecked;

        try
        {
            var result = await CryptoAccountProbe
                .ProbeAsync(_http, broker, credential, _time.GetUtcNow(), ct)
                .ConfigureAwait(false);

            if (result.Ok)
            {
                _logger.LogInformation("{Broker} accepted the API key.", broker);
                return CredentialVerification.Accepted;
            }

            _logger.LogWarning("{Broker} refused the API key: {Detail}", broker, result.Detail);
            return CredentialVerification.Refused(result.Detail);
        }
        catch (OperationCanceledException)
        {
            // Cancelled or timed out. Reporting this as a refusal would tell a user with a perfectly
            // good key to go and regenerate it.
            _logger.LogWarning("Could not reach {Broker} to check the API key.", broker);
            return CredentialVerification.NotChecked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check the {Broker} API key.", broker);
            return CredentialVerification.NotChecked;
        }
    }

    public void Dispose() => _http.Dispose();
}
