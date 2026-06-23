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
}
