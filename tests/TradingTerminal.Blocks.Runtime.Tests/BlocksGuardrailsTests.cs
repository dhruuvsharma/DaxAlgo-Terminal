using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// The SDK-side guardrails the 2026-09-19 Battlefield runs asked for: what a unit may use without a
/// using, compile errors that name the fix, a gate that fails a page loading files it does not have, and
/// a synthetic market at the scale the units are written for.
/// </summary>
public sealed class BlocksGuardrailsTests
{
    private readonly BlocksUnitCompiler _compiler = new();

    private IReadOnlyList<string> Errors(params StrategyFile[] files) =>
        [.. _compiler.Compile("guard", files).Errors.Select(e => e.Message)];

    // ── the SDK ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encoding_websockets_and_culture_need_no_using()
    {
        // The Nex N2.5 Pro run failed on "'Encoding' does not exist" fourteen times: the network block
        // invites a WebSocket, and a WebSocket needs UTF-8.
        var result = _compiler.Compile("imports", [new StrategyFile("Imports.cs", """
            public sealed class Imports : IUnit
            {
                public UnitInfo Info { get; } = new("Imports");
                public Task StartAsync(IUnitContext context, CancellationToken ct)
                {
                    var socket = new ClientWebSocket();
                    var bytes = Encoding.UTF8.GetBytes(new StringBuilder("a").ToString());
                    var text = 1.5.ToString(CultureInfo.InvariantCulture);
                    socket.Dispose();
                    return Task.CompletedTask;
                }
            }
            """)]);

        result.Errors.Should().BeEmpty(string.Join("\n", result.Errors));
    }

    [Fact]
    public void A_block_id_written_as_a_namespace_is_answered_with_delete_this_using()
    {
        Errors(new StrategyFile("Unit.cs", """
            using DaxAlgo.Blocks.Math;
            public sealed class Unit : IUnit
            {
                public UnitInfo Info { get; } = new("Unit");
                public Task StartAsync(IUnitContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """)).Should().Contain(m => m.Contains("not namespaces") && m.Contains("delete this using"));
    }

    [Fact]
    public void An_unknown_type_is_answered_with_the_imported_ones_it_was_probably_meant_to_be()
    {
        Errors(new StrategyFile("Unit.cs", """
            public sealed class Unit : IUnit
            {
                public UnitInfo Info { get; } = new("Unit");
                private Trade? _last;
                public Task StartAsync(IUnitContext context, CancellationToken ct) => Task.CompletedTask;
            }
            """)).Should().Contain(m => m.Contains("'Trade'") && m.Contains("did you mean") && m.Contains("TradePrint"));
    }

    [Fact]
    public void A_type_declared_in_two_files_names_both()
    {
        Errors(
            new StrategyFile("Unit.cs", """
                public sealed record LiquidationEvent(double Usd);
                public sealed class Unit : IUnit
                {
                    public UnitInfo Info { get; } = new("Unit");
                    public Task StartAsync(IUnitContext context, CancellationToken ct) => Task.CompletedTask;
                }
                """),
            new StrategyFile("Engine.cs", "public sealed record LiquidationEvent(double Usd);"))
            .Should().Contain(m => m.Contains("'LiquidationEvent' is declared in") && m.Contains("Unit.cs") && m.Contains("Engine.cs") && m.Contains("NESTED"));
    }

    [Fact]
    public void The_conventions_say_what_is_imported_and_that_block_ids_are_not_namespaces()
    {
        var conventions = BlockCatalog.Load().Conventions;
        conventions.Should().Contain("`System.Net.WebSockets`").And.Contain("`System.Text`");
        conventions.Should().Contain("Block ids are not namespaces").And.Contain("NESTED");
        conventions.Should().Contain("three@0.170.0");
    }

    // ── the gate ────────────────────────────────────────────────────────────────────────────────

    private const string Fed = """
        public sealed class Fed : IUnit
        {
            public UnitInfo Info { get; } = new("Fed", "", [StrategyParameter.Instrument("i", "Instrument", new InstrumentId(1))]);
            public Task StartAsync(IUnitContext context, CancellationToken ct)
            {
                context.Market.OnQuote(context.Settings.Instrument("i"), q => context.Ui.Send("state", new { bid = q.Bid }));
                return Task.CompletedTask;
            }
        }
        """;

    [Fact]
    public async Task A_page_that_loads_a_file_the_unit_does_not_have_fails_and_names_it()
    {
        // Measured: GLM 5.3 Flash's index.html loaded app.js and style.css, neither existed, and the page
        // cleared every rung because an inline script still called dax.ready().
        var gate = new BlocksGate(_compiler, "missing");

        var verdict = await gate.RunAsync(
        [
            new StrategyFile("Fed.cs", Fed),
            new StrategyFile("ui/index.html", """
                <link rel="stylesheet" href="style.css">
                <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">
                <script type="importmap">{ "imports": { "three": "https://cdn.jsdelivr.net/npm/three@0.170.0/build/three.module.js" } }</script>
                <script type="module" src="app.js"></script>
                <script>dax.on("state", s => {}); dax.ready();</script>
                """),
            new StrategyFile("ui/style.css", "body { margin: 0; }"),
        ]);

        verdict.Passed.Should().BeFalse();
        verdict.Report.FailedAt.Should().Be(VerificationRung.DrawProbe);
        verdict.Report.Findings.Should().ContainSingle(f => f.Code == "page.missing-file")
            .Which.File.Should().Be("ui/app.js");
    }

    [Fact]
    public void Imports_between_modules_resolve_relative_to_the_module_and_import_map_names_are_left_alone()
    {
        var missing = PageAssets.Missing(
        [
            new StrategyFile("ui/index.html", """
                <script type="importmap">{ "imports": { "three": "https://x/three.js", "three/addons/": "https://x/addons/" } }</script>
                <script type="module">import { mountScene } from "./scene.js"; import "./hud.js";</script>
                """),
            new StrategyFile("ui/scene.js", """
                import * as THREE from "three";
                import { OrbitControls } from "three/addons/controls/OrbitControls.js";
                import { palette } from "./lib/palette.js";
                // import { gone } from "./commented-out.js";
                export function mountScene(el) { return {}; }
                """),
            new StrategyFile("ui/lib/palette.js", "export const palette = {};"),
        ]);

        missing.Select(m => m.Path).Should().Equal("ui/hud.js");
    }

    [Theory]
    [InlineData("1 script error(s) on the page; the first: boom (scene.js:42)", "ui/scene.js")]
    [InlineData("1 script error(s) on the page; the first: boom (index.html:3)", "ui/index.html")]
    [InlineData("The page did not call dax.ready() within 15 seconds.", null)]
    public void A_page_error_is_addressed_to_the_file_it_came_from(string message, string? file)
    {
        BlocksGate.PageFileIn(message).Should().Be(file);
    }

    // ── the synthetic market ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_drive_feeds_a_book_at_the_scale_units_are_written_for()
    {
        // Until 2026-09-19 the gate fed a market at 100 with five levels of ten lots, and a BTC
        // battlefield photographed against it showed "$0.0M" walls and a round from -395 to 605.
        double lowest = double.MaxValue;
        var levels = 0;
        long biggest = 0;

        var unit = () => new LambdaUnit(
            new UnitInfo("Scale", Settings: [StrategyParameter.Instrument("i", "Instrument", new InstrumentId(1))]),
            context =>
            {
                var i = context.Settings.Instrument("i");
                context.Market.OnQuote(i, q => lowest = Math.Min(lowest, q.Bid));
                context.Market.OnDepth(i, d =>
                {
                    levels = Math.Max(levels, d.Bids.Count);
                    biggest = Math.Max(biggest, d.Bids.Max(l => l.Size));
                });
                return Task.CompletedTask;
            });

        await BlocksDrive.RunAsync(unit, new DriveOptions(Steps: 40));

        lowest.Should().BeGreaterThan(50_000);
        levels.Should().BeGreaterThanOrEqualTo(20);
        biggest.Should().BeGreaterThan(100, "each side carries a wall");
    }
}
