using DaxAlgo.Sandbox.Samples;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The ladder driving a real compiled unit end to end — what replaced <c>StrategyBacktestSmoke</c>.
///
/// <para>The smoke it replaced needed the engine-era registration type, so for a unit written against
/// the contracts the guidance teaches it silently did nothing at all. And against a stub clock and a
/// stub router a strategy could not fail in any way that mattered, because the only thing it could do
/// was place an order into nothing.</para>
/// </summary>
public sealed class AuthoredUnitVerifierTests
{
    private static AuthoredUnit Unit<T>(AuthoringKind kind) => new(kind, typeof(T));

    [Fact]
    public void TheSampleStrategyClearsEveryRung()
    {
        var report = AuthoredUnitVerifier.Verify(
            Unit<MovingAverageCrossKernel>(AuthoringKind.Strategy));

        report.Passed.Should().BeTrue(string.Join("; ", report.Findings.Select(f => f.ToString())));
        report.RungsCleared.Should().BeGreaterThan(2);
    }

    [Fact]
    public void TheSampleVisualizerClearsEveryRung()
    {
        var report = AuthoredUnitVerifier.Verify(
            Unit<SpreadBandVisualizer>(AuthoringKind.Visualizer));

        report.Passed.Should().BeTrue(string.Join("; ", report.Findings.Select(f => f.ToString())));
    }

    [Fact]
    public void AVisualizerSkipsReplayRatherThanPassingIt()
    {
        // It has no book, so there is nothing to replay. Skipped, not passed — it must not collect
        // credit for a rung it never faced.
        var report = AuthoredUnitVerifier.Verify(
            Unit<SpreadBandVisualizer>(AuthoringKind.Visualizer));

        report.Steps.Should().Contain(step =>
            step.Rung == VerificationRung.Replay && step.Outcome == VerificationOutcome.NotApplicable);
    }

    [Fact]
    public void AUnitOnTheRetiredContractIsRefusedRatherThanDriven()
    {
        // It wants an IOrderRouter, which the sandbox does not have and will not grow. Saying so beats
        // reporting a pass that nothing earned.
        var report = AuthoredUnitVerifier.Verify(
            new AuthoredUnit(AuthoringKind.Strategy, typeof(object), UsesRetiredContract: true));

        report.Passed.Should().BeFalse();
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("lifecycle.retired-contract");
        finding.Remedy.Should().Contain("IStrategyKernel");
    }

    [Fact]
    public void AUnitThatCannotBeConstructedFailsWithTheRealReason()
    {
        // TargetInvocationException would otherwise bury the actual cause one level down, and "Exception
        // has been thrown by the target of an invocation" tells an author nothing at all.
        var report = AuthoredUnitVerifier.Verify(Unit<ThrowsInConstructor>(AuthoringKind.Strategy));

        report.Passed.Should().BeFalse();
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("lifecycle.construction-failed");
        finding.Message.Should().Contain("no set-up here");
    }

    [Fact]
    public void AUnitThatDrawsNothingAfterDataIsReported()
    {
        // A strategy may draw nothing at all and be skipped. This one draws a warm-up message forever,
        // which after fourteen bars means it is still explaining itself when it should be showing
        // something.
        var report = AuthoredUnitVerifier.Verify(Unit<StillWaiting>(AuthoringKind.Strategy));

        report.Findings.Should().Contain(finding => finding.Code == "draw.text-only");
    }

    // ── deliberately broken units ───────────────────────────────────────────────────────────────

    private sealed class ThrowsInConstructor : DaxAlgo.Sdk.IStrategyKernel
    {
        public ThrowsInConstructor() =>
            throw new InvalidOperationException("there is no set-up here");

        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(DaxAlgo.Sdk.IStrategyRuntimeContext c, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StillWaiting : DaxAlgo.Sdk.IStrategyKernel
    {
        public Core.Strategies.Parameters.StrategyParameterSchema Schema { get; } =
            Core.Strategies.Parameters.StrategyParameterSchema.Empty;

        public Core.Strategies.StrategyDataRequirement DataRequirement =>
            Core.Strategies.StrategyDataRequirement.Bars;

        public Task OnStartAsync(DaxAlgo.Sdk.IStrategyRuntimeContext c, CancellationToken ct) =>
            Task.CompletedTask;

        public void Draw(DaxAlgo.Sdk.IRenderSurface surface)
        {
            using var panel = surface.Panel("Nothing", DaxAlgo.Sdk.RenderPanelKind.Chart);
            surface.SetStyle(new DaxAlgo.Sdk.RenderStyle(
                surface.Theme(DaxAlgo.Sdk.RenderThemeColor.TextSecondary)));
            surface.Text(8d, 20d, "Waiting for data…");
        }
    }
}
