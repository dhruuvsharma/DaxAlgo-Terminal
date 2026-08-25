using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Phase 0 of #44 — the compiler and the knowledge finally agree on what an author writes.
///
/// <para>They did not. The knowledge pack was rewritten to teach <c>IStrategyKernel</c>, while
/// <c>RoslynStrategyCompiler</c> still discovered only <c>IOrderRoutedStrategy</c> — so a model following
/// its instructions produced code the compiler refused to find, and reported "No public class
/// implementing IOrderRoutedStrategy was found" to an author who had done exactly as they were told.</para>
///
/// <para><c>IOrderRoutedStrategy</c> is not merely an old name. Its own documentation calls it the
/// "engine-facing strategy contract", hands the strategy an <c>IOrderRouter</c>, and describes state
/// transitions "produced by the simulated order book" — the backtest engine archived on 2026-08-17, and
/// a direct route to orders the virtual book replaced. It is still discovered, because Core is a
/// published package and installed plugins implement it, but it is reported as retired.</para>
/// </summary>
public sealed class AuthoredContractDiscoveryTests
{
    private static readonly string[] Ambient =
    [
        "using System;",
        "using System.Threading;",
        "using System.Threading.Tasks;",
        "using DaxAlgo.Sdk;",
        "using TradingTerminal.Core.Domain;",
        "using TradingTerminal.Core.Strategies;",
        "using TradingTerminal.Core.Strategies.Parameters;",
    ];

    private const string Kernel = """
        public sealed class MyKernel : IStrategyKernel
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    private const string Visualizer = """
        public sealed class MyVisualizer : IVisualizer
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    private static StrategyCompileResult Compile(string body) =>
        new RoslynStrategyCompiler().Compile(
            new StrategyScript("test.unit", "Test unit", string.Join("\n", Ambient) + "\n" + body));

    [Fact]
    public void AStrategyKernelIsFoundAndReportedAsCurrent()
    {
        // The case that was broken: exactly what the knowledge pack tells a model to write.
        var result = Compile(Kernel);

        result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        result.Unit.Should().NotBeNull();
        result.Unit!.Kind.Should().Be(AuthoringKind.Strategy);
        result.Unit.UsesRetiredContract.Should().BeFalse();
        result.Unit.ContractName.Should().Be("IStrategyKernel");
    }

    [Fact]
    public void AVisualizerIsFoundAndDistinguishedFromAStrategy()
    {
        var result = Compile(Visualizer);

        result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        result.Unit!.Kind.Should().Be(AuthoringKind.Visualizer);
        result.Unit.ContractName.Should().Be("IVisualizer");
    }

    [Fact]
    public void TheErrorForAnEmptySubmissionNamesTheContractsThatExist()
    {
        // It used to say "No public class implementing IBacktestStrategy was found", sending an author
        // to look for a contract the guidance never mentioned.
        var result = Compile("public sealed class Nothing { }");

        result.Success.Should().BeFalse();
        var message = string.Join(" ", result.Diagnostics.Select(d => d.Message));
        message.Should().Contain("IStrategyKernel").And.Contain("IVisualizer");
        message.Should().NotContain("IOrderRoutedStrategy");
    }

    [Fact]
    public void TwoHostableClassesAreRejectedRatherThanGuessedBetween()
    {
        var result = Compile(Kernel + "\n" + Visualizer);

        result.Success.Should().BeFalse();
        string.Join(" ", result.Diagnostics.Select(d => d.Message))
            .Should().Contain("exactly one");
    }

    [Fact]
    public void ASandboxUnitCompilesButSaysRegistrationIsNotWiredUp()
    {
        // Honest rather than convenient. The unit is sound; the catalog cannot take it yet because
        // registration still runs through the engine-era option type. Reporting a failure would tell an
        // author their correct code was wrong.
        var result = Compile(Kernel);

        result.Success.Should().BeTrue();
        result.Option.Should().BeNull("registration is still on the retired contract");
        result.Authored.Should().NotBeNull("it compiled and its type was resolved");
    }

    [Fact]
    public void TheRetiredContractStillCompilesForInstalledPlugins()
    {
        // Core is a published contract package. Refusing this would break every plugin already out
        // there, which is a different problem from making it the thing we teach.
        var result = Compile("""
            public sealed class OldStrategy : TradingTerminal.Core.Strategies.IOrderRoutedStrategy
            {
                public OldStrategy(Contract contract) { }
                public Task OnStartAsync(TradingTerminal.Core.Time.IClock c, TradingTerminal.Core.Trading.IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
                public Task OnTickAsync(Tick t, TradingTerminal.Core.Time.IClock c, TradingTerminal.Core.Trading.IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(TradingTerminal.Core.Trading.OrderEvent e, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(TradingTerminal.Core.Time.IClock c, TradingTerminal.Core.Trading.IOrderRouter r, CancellationToken ct) => Task.CompletedTask;
            }
            """);

        result.Success.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        result.Unit!.UsesRetiredContract.Should().BeTrue();
        result.UsesRetiredContract.Should().BeTrue();
        result.Option.Should().NotBeNull("the legacy registration path is unchanged");
    }
}
