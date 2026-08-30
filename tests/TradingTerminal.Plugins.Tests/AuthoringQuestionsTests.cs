using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The structured clarifying questions a model may offer instead of prose.
///
/// <para>The builder always supported questions; they arrived as a paragraph and the user typed an
/// answer. Most of those questions have three or four sensible answers the model already has in mind,
/// so offering them as buttons is faster and removes a class of misread reply.</para>
///
/// <para><b>Everything here degrades rather than fails.</b> This parses model output — the least
/// trustworthy input in the system — and a malformed block must cost the buttons, never the turn. A
/// question the user cannot answer at all is far worse than an unstyled one.</para>
/// </summary>
public sealed class AuthoringQuestionsTests
{
    private const string TwoQuestions = """
        I need two things first.

        ```questions
        [
          { "id": "instrument", "question": "Which instrument?", "kind": "single",
            "options": [ { "label": "BTCUSDT perp", "detail": "24/7, deep book" }, { "label": "ES futures" } ] },
          { "id": "exits", "question": "Which exits?", "kind": "multiple",
            "options": [ { "label": "Fixed stop" }, { "label": "Trailing stop" } ], "allowOther": false }
        ]
        ```
        """;

    [Fact]
    public void Questions_are_read_with_their_options_and_modes()
    {
        var parsed = AuthoringQuestions.Parse(TwoQuestions);

        parsed.Should().HaveCount(2);
        parsed[0].Id.Should().Be("instrument");
        parsed[0].Mode.Should().Be(AuthoringAnswerMode.Single);
        parsed[0].Options.Should().HaveCount(2);
        parsed[0].Options[0].Detail.Should().Be("24/7, deep book");
        parsed[1].Mode.Should().Be(AuthoringAnswerMode.Multiple);
        parsed[1].AllowOther.Should().BeFalse();
    }

    [Fact]
    public void Free_text_is_offered_unless_the_model_turns_it_off()
    {
        // A fixed list that happens not to contain what the user wants turns a helpful prompt into a
        // dead end, so the box is on by default and the model has to opt out.
        AuthoringQuestions.Parse(TwoQuestions)[0].AllowOther.Should().BeTrue();
    }

    [Fact]
    public void The_block_is_stripped_from_what_the_user_reads()
    {
        // The prose is the model explaining itself; the JSON is plumbing. Showing both means showing
        // the user a data structure.
        var visible = AuthoringQuestions.StripBlock(TwoQuestions);

        visible.Should().Be("I need two things first.");
        visible.Should().NotContain("```");
    }

    [Fact]
    public void The_reply_that_was_reported_as_broken_parses()
    {
        // Verbatim from a real kimi-k3 turn on "create a tradingview like chart with RSI and Triple
        // EMA". The model did everything right — four well-formed questions with sensible options —
        // and the user still saw raw JSON, because the agent path never called this parser. Pinned as
        // the payload rather than a tidy fixture: it is the one that was actually produced.
        const string reported = """
            This brief is close to buildable, but a few choices change what gets written.

            ```questions
            [
              { "id": "kind", "question": "Should this just draw the chart, or also trade the signals?",
                "kind": "single",
                "options": [ { "label": "Visualizer only", "detail": "Candles + TEMA + RSI panel, no trading" },
                             { "label": "Strategy", "detail": "Same chart, plus it takes positions" } ] },
              { "id": "instrument", "question": "Which instrument?", "kind": "single",
                "options": [ { "label": "BTCUSDT perp" }, { "label": "ETHUSDT perp" },
                             { "label": "ES futures" }, { "label": "SPY" } ] },
              { "id": "timeframe", "question": "Which bar timeframe?", "kind": "single",
                "options": [ { "label": "1 minute" }, { "label": "5 minutes" },
                             { "label": "15 minutes" }, { "label": "1 hour" } ] },
              { "id": "signals", "question": "If it trades, which entry/exit rules?", "kind": "multiple",
                "options": [ { "label": "Price crosses TEMA" }, { "label": "RSI overbought/oversold" },
                             { "label": "RSI trend filter" }, { "label": "Fixed stop + target" } ] }
            ]
            ```

            Defaults I'll take unless you say otherwise: TEMA and RSI periods of 14.
            """;

        var parsed = AuthoringQuestions.Parse(reported);

        parsed.Should().HaveCount(4);
        parsed[0].Options.Should().HaveCount(2);
        parsed[1].Options.Should().HaveCount(4);
        parsed[3].Mode.Should().Be(AuthoringAnswerMode.Multiple);

        var visible = AuthoringQuestions.StripBlock(reported);
        visible.Should().StartWith("This brief is close to buildable");
        visible.Should().Contain("Defaults I'll take");
        visible.Should().NotContain("\"options\"");
    }

    // ── degradation ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Just a plain question. Which instrument?")]
    [InlineData("```questions\nnot json at all\n```")]
    [InlineData("```questions\n{ \"broken\": \n```")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_unparseable_yields_no_questions_rather_than_throwing(string? reply)
    {
        AuthoringQuestions.Parse(reply).Should().BeEmpty();
    }

    [Fact]
    public void A_question_with_no_options_is_dropped()
    {
        // It is a prose question wearing a block. Rendered literally it is an empty chip row, which
        // reads as a broken control rather than as a question to type an answer to.
        var parsed = AuthoringQuestions.Parse("""
            ```questions
            [ { "id": "a", "question": "Which instrument?", "options": [] } ]
            ```
            """);

        parsed.Should().BeEmpty();
    }

    [Fact]
    public void A_question_missing_its_id_still_works()
    {
        // The id is convenience for composing the reply, not contract. Losing the question over it
        // would trade a cosmetic problem for a functional one.
        var parsed = AuthoringQuestions.Parse("""
            ```questions
            [ { "question": "Which timeframe?", "options": [ { "label": "1m" } ] } ]
            ```
            """);

        parsed.Should().ContainSingle();
        parsed[0].Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Runaway_questions_and_options_are_bounded()
    {
        var many = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{ "id": "q{{i}}", "question": "Q{{i}}?", "options": [ { "label": "yes" } ] }"""));
        var wide = string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{ "label": "o{{i}}" }"""));

        AuthoringQuestions.Parse($"```questions\n[{many}]\n```")
            .Should().HaveCount(AuthoringQuestions.MaximumQuestions);

        AuthoringQuestions.Parse($"```questions\n[{{ \"question\": \"Q?\", \"options\": [{wide}] }}]\n```")[0]
            .Options.Should().HaveCount(AuthoringQuestions.MaximumOptions);
    }

    // ── composing the reply ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Answers_go_back_as_labelled_prose()
    {
        // The model asked in prose and reasons in prose. Handing it a data structure it did not ask
        // for is a second format to get right for no gain.
        var questions = AuthoringQuestions.Parse(TwoQuestions);
        var composed = AuthoringQuestions.ComposeAnswer(
            questions,
            new Dictionary<string, string>
            {
                ["instrument"] = "BTCUSDT perp",
                ["exits"] = "Fixed stop, Trailing stop",
            });

        composed.Should().Contain("Which instrument: BTCUSDT perp");
        composed.Should().Contain("Which exits: Fixed stop, Trailing stop");
        composed.Should().NotContain("No preference");
    }

    [Fact]
    public void Unanswered_questions_are_named_rather_than_dropped()
    {
        // A model that asked two things and got one back will otherwise assume a default for the other
        // and never say which — the silent-decision failure this codebase keeps running into.
        var questions = AuthoringQuestions.Parse(TwoQuestions);
        var composed = AuthoringQuestions.ComposeAnswer(
            questions,
            new Dictionary<string, string> { ["instrument"] = "ES futures" });

        composed.Should().Contain("No preference on: Which exits");
        composed.Should().Contain("say what you chose");
    }

    [Theory]
    [InlineData(AuthoringKind.Strategy)]
    [InlineData(AuthoringKind.Visualizer)]
    public void Every_kind_brief_ends_by_telling_the_model_to_offer_its_answers(AuthoringKind kind)
    {
        // The shared pack says this too, but it sits far earlier in the prompt. A model asked to write
        // a long specification reliably forgot it by the time it finished writing one — observed:
        // a full TradingView-style spec, ending in a request for confirmation, with no options and
        // nothing for the user to click. This is the last instruction before the answer.
        var composed = AuthoringKindBrief.Compose("SHARED", kind);

        composed.Should().Contain("```questions");
        composed.Should().Contain("confirm it");
        composed.IndexOf("```questions", StringComparison.Ordinal)
            .Should().BeGreaterThan(
                composed.IndexOf("SHARED", StringComparison.Ordinal),
                "it must come after the cached shared prefix, not inside it");
    }

    [Fact]
    public void Answering_nothing_becomes_an_explicit_you_choose()
    {
        // The Submit button requires at least one answer, so this is not reachable from the UI. It
        // still has to mean something, and "no preference on any of it, pick defaults and tell me what
        // you picked" is a genuinely useful reply — better than an empty message the model has to
        // interpret, and better than refusing to compose one at all.
        var questions = AuthoringQuestions.Parse(TwoQuestions);

        var composed = AuthoringQuestions.ComposeAnswer(questions, new Dictionary<string, string>());

        composed.Should().Contain("No preference on:");
        composed.Should().Contain("Which instrument");
        composed.Should().Contain("Which exits");
        composed.Should().Contain("say what you chose");
    }
}
