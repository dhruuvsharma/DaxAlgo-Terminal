using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using TradingTerminal.UI.Controls.Render;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.App.Basic.Tests;

/// <summary>
/// Renders an authored unit to a PNG, so what Hyperion built can be LOOKED AT rather than described.
///
/// <para>The benchmark can say a unit clears the ladder, and the ladder can say primitives were
/// emitted inside a panel with finite coordinates and a theme role. None of that answers "is it a
/// good window", which is the question the goal is actually about and the one only a person can
/// settle. So this produces the picture and stops.</para>
///
/// <para><b>It draws through the real path.</b> The unit is compiled by the sandbox compiler, driven
/// by <see cref="SyntheticDrive"/> — the same lifecycle the verifier uses, so the picture is of a unit
/// that has actually seen depth, a tape and a hundred and twenty bars — and rendered by
/// <see cref="AuthoredUnitLayoutHost"/>, which is the control the window uses. A screenshot from a
/// mock would be a picture of the mock.</para>
/// </summary>
public sealed class AuthoredUnitScreenshot(ITestOutputHelper output)
{
    /// <summary>A realistic window body. Small enough to fit on screen, large enough that a
    /// three-panel layout is not measuring its own margins.</summary>
    private const int Width = 1280;
    private const int Height = 760;

    [WpfFact]
    public void Render_the_unit_named_by_the_environment()
    {
        var source = Environment.GetEnvironmentVariable("HYPERION_SHOT_SOURCE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            output.WriteLine("Set HYPERION_SHOT_SOURCE to a generated .cs. Nothing to do.");
            return;
        }

        var destination = Environment.GetEnvironmentVariable("HYPERION_SHOT_OUT")
            ?? Path.ChangeExtension(source, ".png");

        var compiled = new RoslynStrategyCompiler().Compile(new StrategyScript(
            "shot", "Shot", [new StrategyFile(Path.GetFileName(source), File.ReadAllText(source))]));

        Assert.True(compiled.Success, string.Join(
            " | ", compiled.Errors.Select(d => $"{d.Id} {d.Location} {d.Message}")));
        Assert.NotNull(compiled.Unit);

        var instance = Activator.CreateInstance(compiled.Unit!.Type)!;

        // Driven before drawing, and this is the whole reason the picture is worth looking at: a unit
        // that has seen no data draws "waiting for depth", which is the same frame a broken one draws.
        switch (instance)
        {
            case IVisualizer visualizer: SyntheticDrive.Run(visualizer); break;
            case IStrategyKernel kernel: SyntheticDrive.Run(kernel); break;
            default: Assert.Fail($"{compiled.Unit.Type.Name} is neither a visualizer nor a kernel.");
                break;
        }

        var host = new AuthoredUnitLayoutHost
        {
            Layout = Layout(instance),
            Draw = Draw(instance),
            ThemeResolver = Dark,
            Clock = () => new DateTime(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc),
        };

        // Wrapped in a Border, and it is not decoration. A ContentControl's Background is painted by a
        // template it does not have, and RenderSurfaceView lays a TRANSPARENT ground over its whole
        // area so the pointer is over something everywhere — so a bitmap of the host alone composites
        // every translucent fill onto nothing. The first screenshots came out with white gridlines and
        // pale grey tiles, and the unit looked wrong when the harness was.
        var frame = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Dark(RenderThemeColor.Background)),
            Child = host,
        };

        frame.Measure(new Size(Width, Height));
        frame.Arrange(new Rect(0d, 0d, Width, Height));
        frame.UpdateLayout();

        var bitmap = new RenderTargetBitmap(Width, Height, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(frame);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(destination)) encoder.Save(file);

        output.WriteLine($"{compiled.Unit.Type.Name} -> {destination}");
        Assert.True(new FileInfo(destination).Length > 0);
    }

    private static DaxAlgo.Sdk.Layout.UnitLayout? Layout(object unit) => unit switch
    {
        IVisualizer visualizer => visualizer.Layout,
        IStrategyKernel kernel => kernel.Layout,
        _ => null,
    };

    private static Action<IRenderSurface>? Draw(object unit) => unit switch
    {
        IVisualizer visualizer => visualizer.Draw,
        IStrategyKernel kernel => kernel.Draw,
        _ => null,
    };

    /// <summary>
    /// The terminal's dark palette, by hand.
    ///
    /// <para>The window resolves these from the application's resource dictionaries, which a test host
    /// does not load — and an unresolved <c>DynamicResource</c> is transparent, so a screenshot taken
    /// that way would be a black rectangle with the author blamed for it.</para>
    /// </summary>
    private static Color Dark(RenderThemeColor token) => token switch
    {
        RenderThemeColor.Text => Color.FromRgb(0xE6, 0xED, 0xF3),
        RenderThemeColor.TextSecondary => Color.FromRgb(0x8B, 0x94, 0x9E),
        RenderThemeColor.Background => Color.FromRgb(0x0D, 0x11, 0x17),
        RenderThemeColor.Surface => Color.FromRgb(0x16, 0x1B, 0x22),
        RenderThemeColor.Grid => Color.FromRgb(0x21, 0x26, 0x2D),
        RenderThemeColor.Border => Color.FromRgb(0x30, 0x36, 0x3D),
        RenderThemeColor.Accent => Color.FromRgb(0x58, 0xA6, 0xFF),
        RenderThemeColor.Bullish => Color.FromRgb(0x3F, 0xB9, 0x50),
        RenderThemeColor.Bearish => Color.FromRgb(0xF8, 0x51, 0x49),
        RenderThemeColor.Warning => Color.FromRgb(0xD2, 0x99, 0x22),
        _ => Color.FromRgb(0x6E, 0x76, 0x81),
    };
}
