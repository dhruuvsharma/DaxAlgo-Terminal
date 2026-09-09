using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>A rendered unit: the picture a user would actually see.</summary>
/// <param name="Png">PNG bytes.</param>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="Hash">
/// A content hash of the render.
///
/// <para>It is what lets a critic be skipped when nothing has changed. A model call over a picture
/// identical to the one already judged buys nothing and costs the user the same as one that would —
/// and re-running a gate over an unchanged workspace is the specific waste Prime Agent's autonomous
/// gate names, and the specific shape of the 140 identical repair turns this harness measured.</para>
/// </param>
public sealed record UnitRaster(byte[] Png, int Width, int Height, string Hash)
{
    /// <summary>What the wire calls it.</summary>
    public const string MediaType = "image/png";
}

/// <summary>
/// Draws a compiled unit into a bitmap, off-screen.
///
/// <para><b>An interface here and an implementation elsewhere, because this assembly must not reference
/// WPF.</b> <c>DaxAlgo.Codegen</c> was split out of Infrastructure precisely so the <c>daxalgo
/// strategy</c> CLI could reuse the codegen without dragging a UI framework into a global tool, and
/// rasterising is the one part of verification that genuinely needs one.</para>
///
/// <para>So a shell registers the WPF implementation and the CLI registers
/// <see cref="NullUnitRasterizer"/>. A critic with no raster judges the drawing commands instead and
/// says so, which is worse than looking at the picture and far better than not looking at all.</para>
/// </summary>
public interface IUnitRasterizer
{
    /// <summary>True when this rasterizer can actually produce a picture.</summary>
    bool CanRender { get; }

    /// <summary>
    /// Renders one frame.
    /// </summary>
    /// <param name="draw">The unit's draw call — the same delegate the ladder drove and the preview
    /// shows, so what a critic judges and what the user sees cannot disagree.</param>
    /// <param name="layout">The unit's panel layout, so the picture has the shape its window will.</param>
    /// <param name="width">Pixels. A trading panel judged at 200px wide would be marked down for
    /// crowding that exists only in the probe.</param>
    /// <param name="height">Pixels.</param>
    /// <returns>The render, or null when this rasterizer cannot produce one.</returns>
    Task<UnitRaster?> RenderAsync(
        Action<IRenderSurface> draw,
        UnitLayout? layout = null,
        int width = 1280,
        int height = 800,
        CancellationToken ct = default);
}

/// <summary>
/// The rasterizer for a host that has no UI: the CLI, and any test that is not about pixels.
///
/// <para>It returns null rather than throwing, and that difference is the whole design. A verification
/// pass that fell over because the host had no window would make the CLI unable to check anything;
/// returning null lets every caller degrade to the drawing commands, which is the case they already
/// handle for a provider that cannot see images.</para>
/// </summary>
public sealed class NullUnitRasterizer : IUnitRasterizer
{
    public static NullUnitRasterizer Instance { get; } = new();

    public bool CanRender => false;

    public Task<UnitRaster?> RenderAsync(
        Action<IRenderSurface> draw,
        UnitLayout? layout = null,
        int width = 1280,
        int height = 800,
        CancellationToken ct = default) => Task.FromResult<UnitRaster?>(null);
}
