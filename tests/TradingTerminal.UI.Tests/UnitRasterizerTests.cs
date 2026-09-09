using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Authoring.Rasterizer;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Rendering a compiled unit to a PNG, off-screen — the picture a vision critic is actually shown.
///
/// <para>Everything below the critics proves a unit emitted the right drawing COMMANDS. That is not the
/// same claim as "a picture came out", and the gap between them is exactly where a broken template, an
/// unresolved binding or a mis-sized panel lives. So this drives the real control tree, really lays it
/// out, and really rasterises it — and then looks at the bytes.</para>
///
/// <para><b>What it does not prove:</b> that a model can see the result. Nothing here calls a provider.
/// The wire shapes that carry an image are pinned in <c>Plugins.Tests</c>, against a fake.</para>
/// </summary>
public sealed class UnitRasterizerTests
{
    /// <summary>A colour no theme brush uses, so finding it in the raster can only mean the unit drew it.</summary>
    private static readonly Color Sentinel = Color.FromRgb(0xFF, 0x00, 0xFF);

    private static Action<IRenderSurface> Sentinelled() => surface =>
    {
        using (surface.Panel("p", RenderPanelKind.Canvas))
        {
            surface.SetStyle(new RenderStyle(new RenderColor(0xFF, 0x00, 0xFF)));
            surface.Rect(10d, 10d, 200d, 140d);
        }
    };

    private static HashSet<Color> ColoursOf(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0d);

        var stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        var colours = new HashSet<Color>();
        for (var offset = 0; offset < buffer.Length; offset += 4)
        {
            if (buffer[offset + 3] == 0) continue;
            colours.Add(Color.FromRgb(buffer[offset + 2], buffer[offset + 1], buffer[offset]));
        }

        return colours;
    }

    [WpfFact]
    public async Task WhatAUnitDrawsSurvivesTheTripToAPng()
    {
        using var rasterizer = new WpfUnitRasterizer();

        var raster = await rasterizer.RenderAsync(Sentinelled(), width: 640, height: 480);

        Assert.NotNull(raster);
        Assert.Equal(640, raster!.Width);
        Assert.Equal(480, raster.Height);
        Assert.Contains(Sentinel, ColoursOf(raster.Png));
    }

    [WpfFact]
    public async Task ThePngIsAPngRatherThanWhateverWasInTheBuffer()
    {
        // The bytes go to a provider that will reject them on their magic number, and a wrong media
        // type reads to a user as a bad key rather than as a broken encoder.
        using var rasterizer = new WpfUnitRasterizer();

        var raster = await rasterizer.RenderAsync(Sentinelled());

        Assert.NotNull(raster);
        Assert.Equal("image/png", UnitRaster.MediaType);
        Assert.True(raster!.Png.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, raster.Png[..4]);
    }

    [WpfFact]
    public async Task TheSamePictureHashesTheSameAndADifferentOneDoesNot()
    {
        // This is what lets a critic be skipped when nothing has changed. A model call over a picture
        // identical to the one already judged buys nothing and costs the user the same as one that
        // would — and re-running a gate over an unchanged workspace is the exact waste behind the 140
        // identical repair turns this harness was built to stop.
        using var rasterizer = new WpfUnitRasterizer();

        var first = await rasterizer.RenderAsync(Sentinelled(), width: 320, height: 240);
        var again = await rasterizer.RenderAsync(Sentinelled(), width: 320, height: 240);
        var other = await rasterizer.RenderAsync(
            surface =>
            {
                using (surface.Panel("p", RenderPanelKind.Canvas))
                {
                    surface.SetStyle(new RenderStyle(new RenderColor(0x00, 0xFF, 0x00)));
                    surface.Rect(10d, 10d, 200d, 140d);
                }
            },
            width: 320,
            height: 240);

        Assert.Equal(first!.Hash, again!.Hash);
        Assert.NotEqual(first.Hash, other!.Hash);
    }

    [WpfFact]
    public async Task AUnitThatThrowsWhileDrawingStillProducesTheFrameItGotTo()
    {
        // The host already swallows a mid-frame throw and keeps the partial picture — a visualizer that
        // dies must not take the window with it. So the raster comes back, and it is the RIGHT thing to
        // hand a critic: it is what the user would be looking at.
        //
        // Reporting the fault belongs to the draw probe, which drives the same delegate and says
        // `draw.threw` with the exception on it. Two places reporting one fault is how a repair turn
        // gets sent to fix a symptom.
        using var rasterizer = new WpfUnitRasterizer();

        var raster = await rasterizer.RenderAsync(
            _ => throw new InvalidOperationException("boom"), width: 320, height: 240);

        Assert.NotNull(raster);
        Assert.True(rasterizer.CanRender, "one bad unit must not retire the render thread");

        // And the next unit still renders on the same thread.
        var after = await rasterizer.RenderAsync(Sentinelled(), width: 320, height: 240);
        Assert.Contains(Sentinel, ColoursOf(after!.Png));
    }

    [WpfFact]
    public async Task AnAbsurdSizeIsClampedRatherThanAllocated()
    {
        // The width comes from a caller and the buffer is width × height × 4, so an unchecked 20,000
        // pixel request is a 1.6 GB allocation on a machine that is also running a trading terminal.
        using var rasterizer = new WpfUnitRasterizer();

        var raster = await rasterizer.RenderAsync(Sentinelled(), width: 20_000, height: 20_000);

        Assert.NotNull(raster);
        Assert.Equal(WpfUnitRasterizer.MaximumSide, raster!.Width);
    }

    [WpfFact]
    public async Task ARasterizerThatIsDisposedStopsRatherThanThrowing()
    {
        var rasterizer = new WpfUnitRasterizer();
        await rasterizer.RenderAsync(Sentinelled(), width: 160, height: 120);

        rasterizer.Dispose();

        Assert.False(rasterizer.CanRender);
        Assert.Null(await rasterizer.RenderAsync(Sentinelled()));

        // Idempotent: a host that disposes twice on shutdown is normal, not a fault.
        rasterizer.Dispose();
    }

    [Fact]
    public async Task TheNullRasterizerRendersNothingAndSaysSo()
    {
        // The CLI's implementation. Returning null rather than throwing is what lets a headless host
        // still verify everything else.
        Assert.False(NullUnitRasterizer.Instance.CanRender);
        Assert.Null(await NullUnitRasterizer.Instance.RenderAsync(Sentinelled(), UnitLayout.Single));
    }
}
