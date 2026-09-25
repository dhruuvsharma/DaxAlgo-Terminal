using System.Net;
using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.ETrade;
using TradingTerminal.Infrastructure.Ig;
using TradingTerminal.Infrastructure.Questrade;
using TradingTerminal.Infrastructure.RobinhoodCrypto;
using TradingTerminal.Infrastructure.Saxo;
using TradingTerminal.Infrastructure.Schwab;
using TradingTerminal.Infrastructure.Tastytrade;
using TradingTerminal.Infrastructure.TradeStation;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Each US and global sign-in, sent made-up credentials against the real broker.
///
/// <para><b>What this proves without an account.</b> A refusal in the broker's own words means the host
/// resolves, the path exists and the request is shaped well enough to be read — an adapter written from
/// documentation is most often wrong in exactly those places. What it cannot prove is that a real account's
/// data parses; that is what <see cref="BrokerStatus.Unverified"/> stays for.</para>
///
/// <para><b>Opt-in.</b> Runs only with <c>DAXALGO_LIVE_VENUES=1</c>, one request per broker. Tradovate is left
/// out: a failed credentials sign-in there earns a penalty ticket for the machine.</para>
/// </summary>
public sealed class GlobalBrokerRefusalLiveRun(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DAXALGO_LIVE_VENUES") == "1";

    public static TheoryData<string> Brokers => ["Schwab", "TradeStation", "tastytrade", "E*TRADE", "Saxo", "IG", "Questrade", "Robinhood"];

    [Theory]
    [MemberData(nameof(Brokers))]
    public async Task A_made_up_credential_is_refused_in_the_brokers_words(string broker)
    {
        if (!Enabled) return;

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
        var now = DateTimeOffset.UtcNow;
        var fake = new BrokerCredential("daxalgo-made-up-key", "daxalgo-made-up-secret") { Account = "daxalgo-made-up-user" };

        string detail;
        if (broker == "E*TRADE")
        {
            try
            {
                await new ETradeSignIn().SignInUrlAsync(http, fake, "", now, CancellationToken.None);
                detail = "(issued a request token to a made-up key)";
            }
            catch (InvalidOperationException ex)
            {
                detail = ex.Message;
            }
        }
        else
        {
            IBrokerSignIn signIn = broker switch
            {
                "Schwab" => new SchwabSignIn(),
                "TradeStation" => new TradeStationSignIn(),
                "tastytrade" => new TastytradeSignIn(),
                "Saxo" => new SaxoSignIn(),
                "IG" => new IgSignIn(),
                "Questrade" => new QuestradeSignIn(),
                _ => new RobinhoodCryptoSignIn(),
            };
            var app = broker == "Robinhood" ? fake with { Secret = "TM0Imyj/ltqdtsNG7BFOD1uKMZ81q6Yk2oz27U+4pvs=" } : fake;
            var issue = await signIn.SignInAsync(http, app, "code=daxalgo-made-up-code", "https://127.0.0.1", now, CancellationToken.None);
            issue.Ok.Should().BeFalse();
            detail = issue.Detail;
        }

        output.WriteLine($"{broker}: {detail}");
        detail.Should().NotContain("HTTP 404", "a 404 means the path is wrong, not the credential")
            .And.NotContain("No such host").And.NotContain("could not be resolved");
    }
}
