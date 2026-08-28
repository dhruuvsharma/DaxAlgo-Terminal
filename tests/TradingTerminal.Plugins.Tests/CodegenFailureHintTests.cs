using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The sentence appended to a provider's rejection.
///
/// <para><b>Why a rejection needs help at all.</b> An unknown model and a bad key both come back as a
/// 4xx with a terse body, and the two have completely different fixes. The expensive one is a model id
/// in the wrong shape: gateways fronting several vendors publish ids for their own config format —
/// OpenCode Zen documents <c>opencode/&lt;id&gt;</c> — while the OpenAI-compatible <c>model</c> field
/// wants the bare id. Both look like a name, so the wrong one is refused in a way that reads as "your
/// key has no access to this model", and the user goes off to check a key that was always fine.</para>
/// </summary>
public sealed class CodegenFailureHintTests
{
    [Fact]
    public void A_prefixed_model_id_is_offered_in_its_bare_form()
    {
        var hint = OpenAiCompatibleCodegenClient.Hint(
            404, """{"error":{"message":"The model `opencode/big-pickle` does not exist"}}""",
            "opencode/big-pickle");

        hint.Should().Contain("big-pickle").And.Contain("bare id");
        hint.Should().NotContain("API key", "this is not a credential problem and must not send the user to one");
    }

    [Fact]
    public void A_bare_model_id_that_is_unknown_says_so_without_inventing_an_alternative()
    {
        // Nothing to suggest — there is no prefix to strip. Offering one anyway would be a guess
        // dressed as advice.
        var hint = OpenAiCompatibleCodegenClient.Hint(
            404, """{"error":{"message":"model not found"}}""", "big-pickle");

        hint.Should().Contain("does not know the model").And.Contain("provider's model list");
        hint.Should().NotContain("try");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void An_auth_failure_points_at_the_key(int status)
    {
        var hint = OpenAiCompatibleCodegenClient.Hint(status, """{"error":"Unauthorized"}""", "big-pickle");

        hint.Should().Contain("API key");
    }

    [Fact]
    public void An_ordinary_server_error_adds_nothing()
    {
        // A 500 is the provider's problem. Appending advice would be noise on top of an outage.
        OpenAiCompatibleCodegenClient.Hint(500, "upstream error", "big-pickle").Should().BeEmpty();
    }

    [Fact]
    public void A_rate_limit_is_not_mistaken_for_a_key_problem()
    {
        // 429 bodies routinely mention the key or the account; sending the user to re-check a working
        // key while they are merely being throttled wastes the one thing they are short of.
        OpenAiCompatibleCodegenClient.Hint(429, """{"error":"rate limit exceeded"}""", "big-pickle")
            .Should().BeEmpty();
    }

    [Fact]
    public void An_empty_body_never_throws()
    {
        OpenAiCompatibleCodegenClient.Hint(400, string.Empty, "big-pickle").Should().BeEmpty();
    }

    // ── base URL normalisation ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://integrate.api.nvidia.com/v1/chat/completions", "https://integrate.api.nvidia.com/v1")]
    [InlineData("https://api.openai.com/v1/chat/completions/", "https://api.openai.com/v1")]
    [InlineData("https://x.test/v1/completions", "https://x.test/v1")]
    [InlineData("https://x.test/v1/responses", "https://x.test/v1")]
    public void A_pasted_full_endpoint_is_reduced_to_its_base(string pasted, string expected)
    {
        // The natural mistake, and one that already cost a setup. Every provider quickstart shows the
        // FULL endpoint — NVIDIA names the variable invoke_url — so that is what gets pasted into a
        // field labelled "base URL". The client then requests /chat/completions/chat/completions and
        // 404s with nothing worth reading, which looks like a broken provider or a rejected key.
        OpenAiCompatibleCodegenClient.NormaliseBaseUrl(pasted).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://integrate.api.nvidia.com/v1", "https://integrate.api.nvidia.com/v1")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    public void A_correct_base_is_left_alone(string given, string expected)
    {
        OpenAiCompatibleCodegenClient.NormaliseBaseUrl(given).Should().Be(expected);
    }

    [Fact]
    public void A_missing_base_stays_empty_rather_than_throwing()
    {
        // Empty is a real state — a provider that has not been set up yet — and IsAvailable reads it.
        OpenAiCompatibleCodegenClient.NormaliseBaseUrl(null).Should().BeEmpty();
        OpenAiCompatibleCodegenClient.NormaliseBaseUrl("   ").Should().BeEmpty();
    }
}
