using System.Net;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A rate limit is the provider asking for a pause, and it was not retried at all.
///
/// <para><b>Measured, not imagined.</b> A batch of six briefs on a free tier spent thirty-four minutes
/// on the first and then failed the remaining five in UNDER HALF A SECOND EACH, producing nothing,
/// because the first run had used the quota. Five briefs' worth of an overnight window, thrown away on
/// the one failure that says exactly how to fix it.</para>
///
/// <para>Two kinds of "not your fault" need two different answers, which is why this is separate from
/// the gateway retry: a 502 means the connection dropped while the model was thinking and the answer is
/// to send again AT ONCE; a 429 means the opposite, and sending again at once is what was just
/// refused.</para>
/// </summary>
public sealed class RateLimitRetryTests
{
    [Theory]
    [InlineData(429, true)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(200, false)]
    [InlineData(402, false)]
    public void OnlyARateLimitAsksForAWait(int status, bool limited)
    {
        OpenAiCompatibleCodegenClient.IsRateLimited(status).Should().Be(limited);
    }

    [Fact]
    public void APaymentFailureIsNotRetriedAtAll()
    {
        // 402 is the one that must NOT be retried: no amount of waiting adds credit, and retrying it
        // turns an instant, actionable error into a slow one. Measured on this very account.
        OpenAiCompatibleCodegenClient.IsRateLimited(402).Should().BeFalse();
        OpenAiCompatibleCodegenClient.IsTransientGatewayFailure(402).Should().BeFalse();
    }

    [Fact]
    public void TheProvidersOwnRetryAfterIsHonoured()
    {
        // It knows and we do not. Guessing over the top of an explicit header is how a client gets
        // itself limited harder.
        using var response = Limited();
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(7d));

        OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 0)
            .Should().Be(TimeSpan.FromSeconds(7d));
    }

    [Fact]
    public void ADateFormedRetryAfterIsHonouredToo()
    {
        // The header allows both forms, and a provider that sends the date form would otherwise fall
        // through to the guess as though it had said nothing.
        using var response = Limited();
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            DateTimeOffset.UtcNow.AddSeconds(9d));

        OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 0)
            .Should().BeGreaterThan(TimeSpan.FromSeconds(5d))
            .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(10d));
    }

    [Fact]
    public void WithNoHeaderItBacksOff()
    {
        using var response = Limited();

        var first = OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 0);
        var second = OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 1);

        second.Should().BeGreaterThan(first, "a second refusal should wait longer than the first");
    }

    [Fact]
    public void AnAbsurdWaitIsCapped()
    {
        // A provider asking for an hour is one to report to the user, not to wait for in silence.
        using var response = Limited();
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromHours(1d));

        OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 0)
            .Should().Be(OpenAiCompatibleCodegenClient.MaxRetryWait);
    }

    [Fact]
    public void AGatewayFailureIsRetriedWithoutWaiting()
    {
        // The whole point of separating them: a dropped connection is reconnected AT ONCE.
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway);

        OpenAiCompatibleCodegenClient.RetryAfter(response, attempt: 0).Should().Be(TimeSpan.Zero);
    }

    private static HttpResponseMessage Limited() => new((HttpStatusCode)429);
}
