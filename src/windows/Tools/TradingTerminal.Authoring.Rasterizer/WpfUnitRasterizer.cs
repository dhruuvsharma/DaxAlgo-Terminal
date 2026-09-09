using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using TradingTerminal.UI.Controls.Render;

namespace TradingTerminal.Authoring.Rasterizer;

/// <summary>
/// Renders a compiled unit off-screen, through the same control the real window uses.
///
/// <para><b>Its own STA thread, owned and disposed once.</b> WPF rendering needs a dispatcher on an STA
/// thread, and the two obvious alternatives are both wrong here: rendering on the application's UI
/// thread freezes the pane for the length of a verification pass, and spinning a thread per render
/// leaks one per critic — a run with six critics over four rounds would create twenty-four. So there is
/// exactly one, started on first use and shut down with this object.</para>
///
/// <para>It draws through <see cref="AuthoredUnitLayoutHost"/> rather than through a renderer of its
/// own, deliberately. A second renderer written for verification would drift from the one the user
/// looks at, and would then pass units the host draws differently — which is the failure the whole
/// draw probe exists to catch, reintroduced one layer up.</para>
/// </summary>
public sealed class WpfUnitRasterizer : IUnitRasterizer, IDisposable
{
    /// <summary>Guards <see cref="_dispatcher"/> during start-up, which happens once.</summary>
    private readonly Lock _gate = new();

    private Dispatcher? _dispatcher;
    private Thread? _thread;
    private bool _disposed;

    /// <summary>
    /// The largest render this will attempt, per side.
    ///
    /// <para>A bound rather than a preference: the width comes from a caller and the buffer is
    /// width × height × 4 bytes, so an unchecked 20,000-pixel request is a 1.6 GB allocation on a
    /// machine that is also running a trading terminal.</para>
    /// </summary>
    public const int MaximumSide = 4_096;

    public bool CanRender => !_disposed;

    /// <inheritdoc />
    public async Task<UnitRaster?> RenderAsync(
        Action<IRenderSurface> draw,
        UnitLayout? layout = null,
        int width = 1280,
        int height = 800,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (_disposed) return null;

        var w = Math.Clamp(width, 64, MaximumSide);
        var h = Math.Clamp(height, 64, MaximumSide);

        var dispatcher = EnsureDispatcher();
        if (dispatcher is null) return null;

        try
        {
            return await dispatcher.InvokeAsync(() => RenderOnUiThread(draw, layout, w, h), DispatcherPriority.Normal, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A unit that throws while drawing is the DRAW PROBE'S finding to report, not a crash for
            // the host that was only trying to photograph it. The probe drives the same delegate and
            // says `draw.threw` with the exception on it; returning null here lets the critic fall back
            // to the drawing commands, which is what it does for a model that cannot see pictures.
            return null;
        }
    }

    /// <summary>Builds the control, lays it out, and rasterises it — all on the render thread.</summary>
    private static UnitRaster RenderOnUiThread(
        Action<IRenderSurface> draw, UnitLayout? layout, int width, int height)
    {
        var host = new AuthoredUnitLayoutHost
        {
            Layout = layout ?? UnitLayout.Single,
            Draw = draw,
            Width = width,
            Height = height,

            // Left null on purpose: the host's own default theme is the one the terminal ships, so the
            // picture a critic judges is the picture in the product rather than one in probe colours.
        };

        // A ground to draw on. Without it the raster is transparent wherever the unit painted nothing,
        // which encodes as black in some viewers and white in others — so a critic would be judging
        // contrast the user never sees.
        var ground = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x17)),
            Child = host,
            Width = width,
            Height = height,
        };

        ground.Measure(new Size(width, height));
        ground.Arrange(new Rect(0d, 0d, width, height));
        ground.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32);
        bitmap.Render(ground);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();

        return new UnitRaster(png, width, height, Convert.ToHexString(SHA256.HashData(png)));
    }

    /// <summary>
    /// The render thread, started once.
    ///
    /// <para>Background, so a terminal closing is never held open by it, and named so a hang shows up
    /// as something identifiable in a dump rather than as an anonymous STA thread.</para>
    /// </summary>
    private Dispatcher? EnsureDispatcher()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_dispatcher is { } running) return running;

            using var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "DaxAlgo unit rasterizer",
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            // Bounded. A render thread that never signals is a bug in this class, and waiting for it
            // forever would hang a verification pass on the way to reporting that it could not render.
            return ready.Wait(TimeSpan.FromSeconds(10)) ? _dispatcher : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _dispatcher?.InvokeShutdown();
            _dispatcher = null;
            _thread = null;
        }
    }
}
