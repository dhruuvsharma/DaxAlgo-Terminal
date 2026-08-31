using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What happens when the model answers with prose inside a code fence.
///
/// <para><b>Found by running Hyperion against a real provider for the first time.</b> A fix turn came
/// back as a diagnosis of the previous failure — English, with backticks and em-dashes — wrapped in a
/// <c>// file:</c> fence. The extractor did exactly what the contract says and handed it to the
/// compiler, which reported CS1003, CS1002 and "unexpected character '`'" starting at line 1. The fix
/// loop fed those back, so the next generation tried to FIX THE PROSE, and the one after that wrote a
/// paragraph explaining that the file contained no program at all. Three generations, all spent on
/// text nobody meant as code, and the user pays for every one.</para>
///
/// <para>The turn budget is the user's money. Telling a model its prose has a syntax error is worse
/// than saying nothing, because it answers the question it was asked.</para>
/// </summary>
public sealed class ProseInACodeFenceTests
{
    /// <summary>The real reply, shortened. Every feature that matters is kept: it opens on English,
    /// and it carries characters that cannot appear at the top level of a C# file.</summary>
    private const string Prose =
        "I have to be straight with you: what the file actually contains right now is not C#.\n"
        + "It's a prose message wrapped in a fence labelled `// file: Strategy.cs` — the compiler\n"
        + "isn't failing on bugs in the program, it's failing because there *is no program*.";

    [Fact]
    public void Prose_is_not_mistaken_for_code()
    {
        CodegenCodeExtractor.LooksLikeCode(Prose).Should().BeFalse();
    }

    [Theory]
    [InlineData("public sealed class Book : IVisualizer { }")]
    [InlineData("using DaxAlgo.Sdk;\n\npublic sealed class Book { }")]
    [InlineData("namespace X;\n\nclass Y { }")]
    [InlineData("// file: Book.cs\n// a comment first\npublic sealed class Book { }")]
    [InlineData("[Obsolete]\npublic sealed class Book { }")]
    [InlineData("#nullable enable\npublic sealed class Book { }")]
    [InlineData("internal record struct Row(double Price);")]
    public void Real_code_is_never_refused(string source)
    {
        // The check must not be able to fail a file that would have compiled — a false positive here
        // costs a generation for a unit that was correct, which is the expensive direction.
        CodegenCodeExtractor.LooksLikeCode(source).Should().BeTrue();
    }

    [Fact]
    public void Empty_is_not_code_and_comments_alone_get_the_benefit_of_the_doubt()
    {
        CodegenCodeExtractor.LooksLikeCode(null).Should().BeFalse();
        CodegenCodeExtractor.LooksLikeCode("   ").Should().BeFalse();

        // Comments only is not a program, but it is not prose either. The compiler has the last word.
        CodegenCodeExtractor.LooksLikeCode("// nothing here yet").Should().BeTrue();
    }

    [Fact]
    public async Task The_session_names_it_rather_than_reporting_a_syntax_error()
    {
        // The behaviour that costs money if it is wrong: the fix prompt must say "that was prose",
        // never hand back CS1003 for text that was never meant to compile.
        var client = new ProseClient();
        var session = new StrategyCodegenOrchestrator(new RoslynStrategyCompiler())
            .CreateSession(client, "PACK", "prose", "Prose", maxFixAttempts: 2);

        var turn = await session.SendAsync("an order book");

        turn.Kind.Should().Be(BuildTurnKind.CompileFailed);
        turn.Error.Should().Contain("prose, not C#");

        // Every retry said the same thing. None of them quoted a compiler diagnostic at it.
        client.Prompts.Should().HaveCountGreaterThan(1);
        client.Prompts.Skip(1).Should().OnlyContain(p => p.Contains("prose, not C#", StringComparison.Ordinal));
        client.Prompts.Should().NotContain(p => p.Contains("did not compile", StringComparison.Ordinal));
    }

    /// <summary>Answers with prose in a fence, every time — the loop the live run fell into.</summary>
    private sealed class ProseClient : IStrategyCodegenClient
    {
        public List<string> Prompts { get; } = [];

        public string ProviderId => "prose";
        public string DisplayName => "Prose";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Prompts.Add(request.Messages[^1].Content);
            return Task.FromResult(StrategyCodegenResponse.Ok(
                [new StrategyFile("Strategy.cs", Prose)],
                "```csharp\n// file: Strategy.cs\n" + Prose + "\n```"));
        }
    }
}
