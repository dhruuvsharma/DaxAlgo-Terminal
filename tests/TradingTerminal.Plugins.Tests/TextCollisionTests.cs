using DaxAlgo.Sdk;
using DaxAlgo.Sandbox.Samples;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Two labels drawn on top of each other — the most visible defect in the first benchmark batch, and
/// the one the comparison note wrote off as unseeable.
///
/// <para>A generated strategy gave its tile strip <c>SplitTop(40)</c> when a tile needs about fifty
/// for a label above a value, so the values collided with the table beneath and the window read as
/// corrupt. Every rung passed. "Nothing automated can see this" was wrong — the recording surface
/// holds every label's position and font size, and overlap is arithmetic.</para>
///
/// <para><b>The false-positive half is the half that matters</b>, because AuthoringJudge turns a rung
/// failure into a repair turn: a wrong finding here spends a whole generation rewriting a window that
/// was fine. So the exemplars are the acceptance bar — they are hand-written, reviewed, and drawn at
/// the sizes the library intends, and not one of them may trip this.</para>
/// </summary>
public sealed class TextCollisionTests
{
    public static TheoryData<string> EveryExemplar => new()
    {
        nameof(SpreadBandVisualizer),
        nameof(BookPressureVisualizer),
        nameof(LiquidityBookVisualizer),
        nameof(DepthLandscapeVisualizer),
        nameof(FootprintClusterVisualizer),
        nameof(MovingAverageCrossKernel),
        nameof(RegimeMatrixKernel),
    };

    [Theory]
    [MemberData(nameof(EveryExemplar))]
    public void NoExemplarTripsTheCollisionCheck(string name)
    {
        var unit = Build(name);
        var step = unit switch
        {
            IVisualizer v => Probe(v.Layout, v.Draw, () => SyntheticDrive.Run(Build<IVisualizer>(name)), true),
            IStrategyKernel k => Probe(k.Layout, k.Draw, () => SyntheticDrive.Run(Build<IStrategyKernel>(name)), false),
            _ => throw new InvalidOperationException(name),
        };

        step.Findings.Should().NotContain(
            f => f.Code == "draw.text-collision",
            "the exemplars are the acceptance bar for this check — a hand-written sample tripping it "
            + "means the estimate is too aggressive, and every generated unit would pay for it");
    }

    [Fact]
    public void LabelsStackedOnOneSpotAreCaught()
    {
        // The defect itself, at the size that produced it: four tiles' worth of text crammed into a
        // forty-pixel strip. Without this the check could pass by never firing at all.
        var step = DrawProbe.Run(surface =>
        {
            using var panel = surface.Panel("Cramped", RenderPanelKind.Matrix);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 12d));
            surface.Rect(0d, 0d, 200d, 40d);
            surface.Text(8d, 10d, "Realised P&L");
            surface.Text(8d, 14d, "-1,204.55");
        }, mustDraw: true);

        step.Findings.Should().Contain(f => f.Code == "draw.text-collision");
    }

    [Fact]
    public void LabelsSideBySideAreNot()
    {
        // The other direction, and the one a careless estimate breaks: a row of columns is not a
        // collision however close the columns are.
        var step = DrawProbe.Run(surface =>
        {
            using var panel = surface.Panel("Row", RenderPanelKind.Matrix);
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 10d));
            surface.Rect(0d, 0d, 400d, 40d);
            for (var i = 0; i < 6; i++) surface.Text(10d + i * 60d, 12d, $"col{i}");
        }, mustDraw: true);

        step.Findings.Should().NotContain(f => f.Code == "draw.text-collision");
    }

    private static VerificationStep Probe(
        DaxAlgo.Sdk.Layout.UnitLayout? layout, Action<IRenderSurface> draw, Action drive, bool mustDraw)
    {
        drive();
        return DrawProbe.RunLayout(layout, draw, mustDraw, requirePicture: true);
    }

    private static object Build(string name) => name switch
    {
        nameof(SpreadBandVisualizer) => new SpreadBandVisualizer(),
        nameof(BookPressureVisualizer) => new BookPressureVisualizer(),
        nameof(LiquidityBookVisualizer) => new LiquidityBookVisualizer(),
        nameof(DepthLandscapeVisualizer) => new DepthLandscapeVisualizer(),
        nameof(FootprintClusterVisualizer) => new FootprintClusterVisualizer(),
        nameof(MovingAverageCrossKernel) => new MovingAverageCrossKernel(),
        nameof(RegimeMatrixKernel) => new RegimeMatrixKernel(),
        _ => throw new InvalidOperationException(name),
    };

    private static T Build<T>(string name) => (T)Build(name);
}
