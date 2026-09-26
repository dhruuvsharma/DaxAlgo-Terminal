using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Execution.CTrader;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The cTrader route against the real Open API servers, with made-up app credentials: the TLS host and port, the
/// length-prefixed protobuf framing and the application-auth request all reach cTrader, which refuses the app in
/// its own words. What it cannot prove is that an order is accepted; that takes the owner's demo account.
///
/// <para><b>Opt-in.</b> Runs only with <c>DAXALGO_LIVE_VENUES=1</c>. One connection per environment, no orders.</para>
/// </summary>
public sealed class CTraderOrderRouteLiveRun(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DAXALGO_LIVE_VENUES") == "1";

    private sealed class MadeUp : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) =>
            new("1234_daxalgomadeupclientid", "daxalgo-made-up-secret") { Session = "daxalgo-made-up-token", Account = "1" };
    }

    [Theory]
    [InlineData(RouteEnvironment.Paper)]
    [InlineData(RouteEnvironment.Live)]
    public async Task A_made_up_application_is_refused_by_ctrader_itself(RouteEnvironment environment)
    {
        if (!Enabled) return;
        await using var route = new CTraderOrderRoute(new MadeUp());

        var error = await Record.ExceptionAsync(() => route.ConnectAsync(environment, CancellationToken.None));

        output.WriteLine($"{environment}: {error?.GetType().Name}: {error?.Message}");
        var refusal = Assert.IsType<BrokerOrderRouteException>(error);
        Assert.True(refusal.IsRejection);
    }
}
