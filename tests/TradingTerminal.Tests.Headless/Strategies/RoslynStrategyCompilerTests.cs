using FluentAssertions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public class RoslynStrategyCompilerTests
{
    private static readonly Contract TestContract =
        new("TEST", "STK", "SMART", "USD", "NASDAQ");

    private const string ValidSource = """
        public sealed class MyStrat : IBacktestStrategy
        {
            private readonly Contract _contract;
            public MyStrat(Contract contract) { _contract = contract; }
            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    private const string ParameterizedSource = """
        public sealed class MyParamStrat : IBacktestStrategy
        {
            public static StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("period", "Period", 14, min: 1, max: 100));

            public static IBacktestStrategy Create(Contract c, StrategyParameters p) =>
                new MyParamStrat(c, p.GetInt("period"));

            private readonly Contract _contract;
            private readonly int _period;
            public MyParamStrat(Contract contract) : this(contract, 14) { }
            public MyParamStrat(Contract contract, int period) { _contract = contract; _period = period; }
            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    [Fact]
    public void Compile_valid_strategy_succeeds_and_builds_an_instance()
    {
        var compiler = new RoslynStrategyCompiler();

        var result = compiler.Compile(new StrategyScript("myStrat", "My Strategy", ValidSource));

        result.Success.Should().BeTrue(
            "compile diagnostics: {0}", string.Join("; ", result.Diagnostics));
        result.Option.Should().NotBeNull();
        result.Option!.Id.Should().Be("myStrat");
        result.Option.HasParameters.Should().BeFalse();
        result.Option.Create(TestContract).Should().BeAssignableTo<IBacktestStrategy>();
    }

    [Fact]
    public void Identical_authored_inputs_emit_identical_assembly_bytes()
    {
        var compiler = new RoslynStrategyCompiler();
        var script = new StrategyScript("repeatable", "Repeatable", ValidSource);

        var first = compiler.Compile(script);
        var second = compiler.Compile(script);

        first.Success.Should().BeTrue(string.Join("; ", first.Diagnostics));
        second.Success.Should().BeTrue(string.Join("; ", second.Diagnostics));
        second.Authored!.Image.Should().Equal(first.Authored!.Image,
            "the bundle content root must not change when authored inputs do not change");
        second.Authored.Assembly.GetName().Name.Should().Be(first.Authored.Assembly.GetName().Name);
    }

    [Fact]
    public void Changed_authored_source_gets_a_different_deterministic_identity()
    {
        var compiler = new RoslynStrategyCompiler();

        var first = compiler.Compile(new StrategyScript("repeatable", "Repeatable", ValidSource));
        var second = compiler.Compile(new StrategyScript(
            "repeatable", "Repeatable", ValidSource.Replace("MyStrat", "ChangedStrat", StringComparison.Ordinal)));

        first.Success.Should().BeTrue(string.Join("; ", first.Diagnostics));
        second.Success.Should().BeTrue(string.Join("; ", second.Diagnostics));
        second.Authored!.Assembly.GetName().Name.Should().NotBe(first.Authored!.Assembly.GetName().Name,
            "a regenerated strategy must coexist with its prior in-memory build");
    }

    [Fact]
    public void Compile_invalid_source_fails_with_error_diagnostics()
    {
        var compiler = new RoslynStrategyCompiler();

        var result = compiler.Compile(new StrategyScript("bad", "Bad", "public class X : IBacktestStrategy { "));

        result.Success.Should().BeFalse();
        result.Option.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Compile_parameterized_strategy_exposes_schema_and_honours_values()
    {
        var compiler = new RoslynStrategyCompiler();

        var result = compiler.Compile(new StrategyScript("paramStrat", "Param Strategy", ParameterizedSource));

        result.Success.Should().BeTrue(
            "compile diagnostics: {0}", string.Join("; ", result.Diagnostics));
        result.Option!.HasParameters.Should().BeTrue();

        var values = result.Option.Schema.CreateDefaults();
        values.GetInt("period").Should().Be(14);
        result.Option.Create(TestContract, values).Should().BeAssignableTo<IBacktestStrategy>();
    }

    [Fact]
    public void Compile_source_without_strategy_class_fails_with_bind_error()
    {
        var compiler = new RoslynStrategyCompiler();

        var result = compiler.Compile(new StrategyScript("none", "None", "public class Plain { }"));

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Id == "DAX1000");
    }

    // ── Policy scan on authored code (same gate a dropped-in plugin passes) ───────────────────────

    [Fact]
    public void A_strategy_that_PInvokes_is_blocked_and_never_loaded()
    {
        // Authored source is untrusted the moment an AI or a pasted snippet can write it. A strategy
        // reaching for native code must fail the compile — before Assembly.Load — not run in-process.
        const string pinvokes = """
            using System.Runtime.InteropServices;
            public sealed class Sneaky : IBacktestStrategy
            {
                [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
                private readonly Contract _c;
                public Sneaky(Contract c) { _c = c; }
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) { GetCurrentProcessId(); return Task.CompletedTask; }
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var result = new RoslynStrategyCompiler().Compile(new StrategyScript("sneaky", "Sneaky", pinvokes));

        result.Success.Should().BeFalse("a P/Invoking strategy must not compile-and-register");
        result.Diagnostics.Should().Contain(d =>
            d.Severity == StrategyDiagnosticSeverity.Error && d.Message.Contains("native"));
    }

    [Fact]
    public void A_strategy_that_starts_a_process_is_blocked()
    {
        const string spawns = """
            using System.Diagnostics;
            public sealed class Launcher : IBacktestStrategy
            {
                private readonly Contract _c;
                public Launcher(Contract c) { _c = c; }
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) { Process.Start("cmd.exe"); return Task.CompletedTask; }
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var result = new RoslynStrategyCompiler().Compile(new StrategyScript("launcher", "Launcher", spawns));

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Severity == StrategyDiagnosticSeverity.Error);
    }

    [Fact]
    public void An_ordinary_strategy_scans_clean_and_compiles()
    {
        var result = new RoslynStrategyCompiler().Compile(new StrategyScript("clean", "Clean", ValidSource));

        result.Success.Should().BeTrue();
        result.Diagnostics.Should().NotContain(d => d.Id.StartsWith("DAX2"),
            "a plain strategy trips no policy-scan finding");
    }
}
