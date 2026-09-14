using System.IO;
using System.Runtime.CompilerServices;
using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// Units written as source — the way Hyperion hands them over — compiled with the Blocks profile and,
/// where it matters, run.
/// </summary>
public sealed class BlocksUnitCompilerTests
{
    private readonly BlocksUnitCompiler _compiler = new();

    /// <summary>The two-instrument case, written with no using directives: the ambient set covers it.</summary>
    internal const string ArbitrageSource = """
        public sealed class SpreadArbitrage : IUnit
        {
            public UnitInfo Info { get; } = new("Spread arbitrage", "Sells the rich leg, buys the cheap one.",
            [
                StrategyParameter.Instrument("legA", "Leg A", new InstrumentId(1)),
                StrategyParameter.Instrument("legB", "Leg B", new InstrumentId(2)),
                StrategyParameter.Number("entry", "Entry spread", 1.0, min: 0.01),
            ]);

            private double? _a, _b;
            private readonly Ema _spread = new(20);

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                var legA = context.Settings.Instrument("legA");
                var legB = context.Settings.Instrument("legB");

                context.Market.OnQuote(legA, q => { _a = q.Mid; Decide(context, legA, legB); });
                context.Market.OnQuote(legB, q => { _b = q.Mid; Decide(context, legA, legB); });
                context.Ui.OnOpened(() => Publish(context, legA));
                return Task.CompletedTask;
            }

            private void Decide(IUnitContext context, InstrumentId legA, InstrumentId legB)
            {
                if (_a is not double a || _b is not double b) return;
                _spread.Update(a - b);

                if (a - b > context.Settings.Number("entry") && context.Portfolio.Position(legA).Units == 0)
                {
                    context.Orders.SetTarget(legA, -1);
                    context.Orders.SetTarget(legB, 1);
                }

                Publish(context, legA);
            }

            private void Publish(IUnitContext context, InstrumentId legA) =>
                context.Ui.Send("state", new { spread = _spread.IsReady ? _spread.Value : 0, position = context.Portfolio.Position(legA).Units });
        }
        """;

    /// <summary>An external market, polled over HTTP, drawn by its own page. No orders: a visualizer.</summary>
    internal const string PollerSource = """
        public sealed class PredictionMarketOdds : IUnit
        {
            public UnitInfo Info { get; } = new("Prediction market odds", Settings:
            [
                StrategyParameter.Text("market", "Market slug", "will-it-rain"),
                StrategyParameter.Int("seconds", "Poll every", 30, min: 5, max: 600),
            ]);

            private HttpClient? _http;
            private double _probability;

            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                context.Schedule.Every(TimeSpan.FromSeconds(context.Settings.Int("seconds")), () => _ = PollAsync(context));
                context.Ui.OnOpened(() => context.Ui.Send("odds", new { probability = _probability }));
                return Task.CompletedTask;
            }

            private async Task PollAsync(IUnitContext context)
            {
                try
                {
                    var body = await _http!.GetStringAsync("https://gamma-api.polymarket.com/markets?slug=" + context.Settings.Text("market"));
                    using var json = JsonDocument.Parse(body);
                    var probability = json.RootElement[0].GetProperty("lastTradePrice").GetDouble();
                    context.Schedule.Post(() =>
                    {
                        _probability = probability;
                        context.Ui.Send("odds", new { probability });
                    });
                }
                catch (Exception ex)
                {
                    context.Schedule.Post(() => context.Log.Warn("Poll failed: " + ex.Message));
                }
            }

            public Task StopAsync(CancellationToken ct)
            {
                _http?.Dispose();
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task A_two_instrument_arbitrage_compiles_is_a_strategy_and_trades_both_legs_when_run()
    {
        var result = _compiler.Compile("spread-arb", [new StrategyFile("SpreadArbitrage.cs", ArbitrageSource)]);

        result.Success.Should().BeTrue(string.Join("\n", result.Errors.Select(e => $"{e.Id} {e.Location} {e.Message}")));
        result.UsesOrders.Should().BeTrue("it calls context.Orders");

        var hub = new FakeHub();
        await using var runtime = new BlocksUnitRuntime(result.Factory!, "spread-arb", Host.For(hub));
        await runtime.StartAsync();

        hub.PublishQuote(FakeHub.Quote(1, 102.0, 102.2));
        hub.PublishQuote(FakeHub.Quote(2, 100.0, 100.2));
        hub.PublishQuote(FakeHub.Quote(1, 102.0, 102.2));

        await Host.WaitUntil(() => runtime.Portfolio.Positions.Count == 2, "both legs should be held");
        runtime.Portfolio.Positions.Should().Contain(p => p.Instrument == new InstrumentId(1) && p.Units == -1d);
        runtime.Portfolio.Positions.Should().Contain(p => p.Instrument == new InstrumentId(2) && p.Units == 1d);
    }

    [Fact]
    public void A_unit_that_polls_an_external_api_compiles_and_passes_the_scan()
    {
        var result = _compiler.Compile("odds",
        [
            new StrategyFile("PredictionMarketOdds.cs", PollerSource),
            new StrategyFile("ui/index.html", "<html><body><div id=p></div><script>dax.on('odds', o => p.textContent = o.probability); dax.ready();</script></body></html>"),
            new StrategyFile("ui/style.css", "body { font-family: sans-serif; }"),
        ]);

        result.Success.Should().BeTrue(string.Join("\n", result.Errors.Select(e => $"{e.Id} {e.Location} {e.Message}")));
        result.UsesOrders.Should().BeFalse("it never touches the orders block, so it is a visualizer");
        result.PageFiles!.Select(f => f.Name).Should().BeEquivalentTo(["ui/index.html", "ui/style.css"]);
        result.Diagnostics.Should().NotContain(d => d.Id.Contains("network"), "the Hyperion profile allows the network");
    }

    [Theory]
    [InlineData("System.Diagnostics.Process.Start(\"calc\");", "process")]
    [InlineData("System.IO.File.WriteAllText(\"x.txt\", \"y\");", "fileIo")]
    [InlineData("new System.Threading.Thread(() => { }).Start();", "threading")]
    public void Everything_but_the_network_is_still_refused(string statement, string rule)
    {
        var source = $$"""
            public sealed class Escapes : IUnit
            {
                public UnitInfo Info { get; } = new("Escapes");
                public Task StartAsync(IUnitContext context, CancellationToken ct)
                {
                    {{statement}}
                    return Task.CompletedTask;
                }
            }
            """;

        var result = _compiler.Compile("escapes", [new StrategyFile("Escapes.cs", source)]);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Id == $"DAXSCAN:{rule}");
    }

    [Fact]
    public void The_widget_library_is_not_there_to_reach_for()
    {
        const string source = """
            using DaxAlgo.Sdk.Drawing;

            public sealed class OldHabits : IUnit
            {
                public UnitInfo Info { get; } = new("Old habits");
                public Task StartAsync(IUnitContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var result = _compiler.Compile("old", [new StrategyFile("OldHabits.cs", source)]);

        result.Success.Should().BeFalse("a Blocks unit ships its own page; DaxAlgo.Sdk is not referenced");
        result.Errors.Should().Contain(e => e.Id == "CS0234" || e.Id == "CS0246");
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("ui/../escape.html")]
    [InlineData("ui/app.exe")]
    public void Anything_that_is_not_csharp_must_be_a_page_file_under_ui(string name)
    {
        var result = _compiler.Compile("page", [new StrategyFile("Unit.cs", ArbitrageSource), new StrategyFile(name, "x")]);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Id == "DAXB001");
    }

    [Fact]
    public void Exactly_one_unit_with_a_parameterless_constructor()
    {
        const string none = "public sealed class NotAUnit { }";
        _compiler.Compile("none", [new StrategyFile("A.cs", none)]).Errors.Should().Contain(e => e.Id == "DAXB004");

        const string ctor = """
            public sealed class NeedsArgs(int x) : IUnit
            {
                public UnitInfo Info { get; } = new("Needs args");
                public Task StartAsync(IUnitContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """;
        _compiler.Compile("ctor", [new StrategyFile("B.cs", ctor)]).Errors.Should().Contain(e => e.Id == "DAXB005");
    }

    [Fact]
    public void The_conventions_name_exactly_the_namespaces_the_compiler_imports()
    {
        // Two statements of one fact: what the agent is told is imported, and what is. The authoring
        // pack and the Roslyn compiler once disagreed on exactly this, and every unit that trusted the
        // pack failed its first compile.
        var conventions = File.ReadAllText(Path.Combine(RepositoryRoot(), "sdk", "ai-context", "blocks", "conventions.md"));

        foreach (var ns in BlocksUnitCompiler.AmbientNamespaces)
            conventions.Should().Contain($"`{ns}`", "conventions.md must list {0} as imported", ns);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
