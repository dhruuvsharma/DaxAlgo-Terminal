using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Blocks.WebHost.Tests;

/// <summary>
/// A Hyperion build's gate with the real page probe: compiled from source, driven with its page open in
/// WebView2, photographed for the picture critic — and a broken page reaching the page's owner.
/// </summary>
public sealed class TheGateOpensThePageTests
{
    private static readonly DriveOptions Quick = new(Steps: 30, SettleTime: TimeSpan.FromMilliseconds(400), PageReadyTimeout: TimeSpan.FromSeconds(20));

    private const string Unit = """
        public sealed class Ticker : IUnit
        {
            public UnitInfo Info { get; } = new("Ticker", Settings: [StrategyParameter.Instrument("leg", "Leg", new InstrumentId(1))]);
            private double _last;

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                context.Market.OnQuote(context.Settings.Instrument("leg"), q => { _last = q.Mid; context.Ui.Send("tick", new { mid = _last }); });
                context.Ui.OnOpened(() => context.Ui.Send("tick", new { mid = _last }));
                return Task.CompletedTask;
            }
        }
        """;

    private const string Page = """
        <body style="margin:0;background:#0e1117;color:#e6edf3;font:48px Segoe UI">
          <h1 style="color:#7d8590;font-size:20px">Mid</h1><div id="v" style="color:#3fb950">waiting</div>
          <div style="height:30px;width:70%;background:linear-gradient(90deg,#1f6feb,#a371f7)"></div>
          <script>dax.on('tick', t => v.textContent = t.mid.toFixed(2)); dax.ready();</script>
        </body>
        """;

    [Fact]
    public async Task A_unit_and_its_page_pass_and_come_back_photographed()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var gate = new BlocksGate(new BlocksUnitCompiler(), "ticker", new WebPageProbe(), Quick);
        var verdict = await gate.RunAsync([new StrategyFile("Ticker.cs", Unit), new StrategyFile("ui/index.html", Page)]);

        verdict.Passed.Should().BeTrue(string.Join(" / ", verdict.Report.Findings));
        verdict.Report.Steps.Should().Contain(s => s.Rung == VerificationRung.DrawProbe && s.Outcome == VerificationOutcome.Passed);
        verdict.Picture.Should().NotBeNull();
        PageProbe.IsBlank(verdict.Picture!.Png).Should().BeFalse();
    }

    [Fact]
    public async Task A_page_script_error_fails_the_page_rung_addressed_to_the_page()
    {
        if (!WebUnitView.RuntimeAvailable) return;

        var gate = new BlocksGate(new BlocksUnitCompiler(), "ticker", new WebPageProbe(), Quick);
        var verdict = await gate.RunAsync(
            [new StrategyFile("Ticker.cs", Unit), new StrategyFile("ui/index.html", Page.Replace("v.textContent = t.mid.toFixed(2)", "drawChart(t)", StringComparison.Ordinal))]);

        verdict.Passed.Should().BeFalse();
        verdict.Report.FailedAt.Should().Be(VerificationRung.DrawProbe);
        verdict.Report.Findings.Should().Contain(f => f.Code == "page.threw" && f.File == BlocksGate.PageEntry);
    }
}
