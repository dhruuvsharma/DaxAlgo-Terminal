using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// How long a generation is allowed to take.
///
/// <para>The answer is now "as long as it takes", and the reason is that every other answer is a guess
/// about somebody else's model. The old ten-minute wall was chosen when a build was one request; a hard
/// brief at Max effort is a run of agent turns, and a wall that fires mid-run bills the user for tokens
/// they never receive. The control that belongs here is the Stop button, which is a decision by the
/// person paying.</para>
/// </summary>
public sealed class CodegenTimeoutTests
{
    private static (StrategyCodegenClientFactory Factory, HttpClient Http) Build(int timeoutSeconds)
    {
        var http = new HttpClient();
        var options = new AiCodegenOptions
        {
            TimeoutSeconds = timeoutSeconds,
            Providers =
            {
                ["openai"] = new AiCodegenProvider
                {
                    BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini",
                },
            },
        };

        return (new StrategyCodegenClientFactory(() => http, options, _ => "sk-test"), http);
    }

    [Fact]
    public void TheShippedDefaultIsNoLimit()
    {
        // Zero is the default, and nothing in appsettings overrides it.
        new AiCodegenOptions().TimeoutSeconds.Should().Be(0);
    }

    [Fact]
    public void ZeroMeansTheRequestIsNeverAbandoned()
    {
        var (factory, http) = Build(timeoutSeconds: 0);

        factory.Build("openai", model: null);

        http.Timeout.Should().Be(System.Threading.Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void APositiveValueIsStillHonouredForAnyoneWhoWantsAWall()
    {
        var (factory, http) = Build(timeoutSeconds: 120);

        factory.Build("openai", model: null);

        http.Timeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void AnAbsurdlySmallValueIsFlooredRatherThanFailingEveryRequestInstantly()
    {
        // A typo in a config file should not make the builder look broken.
        var (factory, http) = Build(timeoutSeconds: 3);

        factory.Build("openai", model: null);

        http.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ANegativeValueIsTreatedAsNoLimitRatherThanThrowing()
    {
        // HttpClient.Timeout rejects a negative TimeSpan, so an unguarded pass-through would turn a
        // config typo into a crash on the first generation.
        var (factory, http) = Build(timeoutSeconds: -5);

        factory.Build("openai", model: null);

        http.Timeout.Should().Be(System.Threading.Timeout.InfiniteTimeSpan);
    }
}
