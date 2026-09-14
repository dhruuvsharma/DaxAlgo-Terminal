using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DaxAlgo.Blocks;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>What the probe saw.</summary>
/// <param name="Drive">The drive the unit went through while its page was open.</param>
/// <param name="PageErrors">Script errors on the page.</param>
/// <param name="Png">The page as it looked just before the unit stopped, or null.</param>
/// <param name="Blank">True when the capture is a single flat colour, or missing.</param>
/// <param name="Findings">The drive's findings plus the page's own.</param>
public sealed record PageProbeReport(
    DriveReport Drive,
    IReadOnlyList<PageError> PageErrors,
    byte[]? Png,
    bool Blank,
    IReadOnlyList<DriveFinding> Findings)
{
    public bool Passed => Findings.All(f => f.Severity != DriveSeverity.Failure);
}

/// <summary>How the probe runs.</summary>
/// <param name="Width">Page width in pixels.</param>
/// <param name="Height">Page height in pixels.</param>
/// <param name="Drive">Options for the drive; its Page, HasPage and BeforeStop are set by the probe.</param>
/// <param name="WorkRoot">Where the page files and the probe's WebView2 profile go.</param>
public sealed record PageProbeOptions(int Width = 1280, int Height = 800, DriveOptions? Drive = null, string? WorkRoot = null);

/// <summary>
/// Runs a unit with the page it ships, off-screen, against the synthetic market — the page rung.
///
/// <para>What it adds to the drive: the page loaded and called <c>dax.ready()</c>, it threw no script
/// errors, and what it shows is not a flat, empty rectangle. Each is a deterministic check that costs
/// nothing, and each is a finding a critic looking at a screenshot would otherwise be paid to notice.</para>
///
/// <para><b>Its own STA thread and window per probe</b>, shut down at the end. WebView2 needs a real
/// window handle, and the probe's browser is started with occlusion detection off so a window placed
/// off-screen still paints.</para>
/// </summary>
public static class PageProbe
{
    private const string BrowserArguments = "--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows";

    public static async Task<PageProbeReport> RunAsync(
        Func<IUnit> factory,
        IReadOnlyList<StrategyFile> pageFiles,
        string unitId,
        PageProbeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pageFiles);
        options ??= new PageProbeOptions();

        var root = options.WorkRoot ?? Path.Combine(Path.GetTempPath(), "DaxAlgo", "page-probe");
        var pageFolder = UnitPageFolder.Write(Path.Combine(root, "pages"), $"{unitId}-{Guid.NewGuid():N}", pageFiles);
        var viewOptions = new WebUnitViewOptions(Path.Combine(root, "webview2"), BrowserArguments);

        using var sta = await StaHost.StartAsync("DaxAlgo page probe").ConfigureAwait(false);

        var (window, view) = await sta.InvokeAsync(async () =>
        {
            var view = new WebUnitView(viewOptions);
            var window = new Window
            {
                Content = view,
                Width = options.Width,
                Height = options.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -32000,
                Top = -32000,
                Background = Brushes.Black,
            };

            window.Show();
            await view.LoadAsync(pageFolder, ct);
            return (window, view);
        }).ConfigureAwait(false);

        byte[]? png = null;
        try
        {
            var driveOptions = (options.Drive ?? new DriveOptions()) with
            {
                Page = view,
                HasPage = true,
                BeforeStop = async token =>
                {
                    // Give the page a frame to paint what it was last sent before photographing it.
                    await Task.Delay(250, token).ConfigureAwait(false);
                    png = await sta.InvokeAsync(() => view.CaptureAsync(token)).ConfigureAwait(false);
                },
            };

            var drive = await BlocksDrive.RunAsync(factory, driveOptions, ct).ConfigureAwait(false);
            var errors = view.Errors;
            var blank = IsBlank(png);

            var findings = new List<DriveFinding>(drive.Findings);

            if (errors.Count > 0)
                findings.Add(new DriveFinding(DriveSeverity.Failure, "page.threw",
                    $"{errors.Count} script error(s) on the page; the first: {Describe(errors[0])}",
                    "Fix the script; an error in a dax.on handler is reported with its topic."));

            if (blank && !drive.Findings.Any(f => f.Code == "page.never-ready"))
                findings.Add(new DriveFinding(DriveSeverity.Failure, "page.blank",
                    "The page is a single flat colour after the unit has been sending it data.",
                    "Render something from the state the unit sends, including an empty state while waiting for data."));

            return new PageProbeReport(drive, errors, png, blank, findings);
        }
        finally
        {
            await sta.InvokeAsync(() =>
            {
                view.Dispose();
                window.Close();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            TryDelete(Path.GetDirectoryName(pageFolder)!);
        }
    }

    /// <summary>
    /// True when the picture is one flat colour. Sampled on a grid and quantised, so anti-aliasing noise
    /// in an otherwise empty page does not count as content.
    /// </summary>
    internal static bool IsBlank(byte[]? png)
    {
        if (png is null || png.Length == 0) return true;

        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);

            var colours = new HashSet<int>();
            var step = Math.Max(1, Math.Min(width, height) / 64);
            for (var y = 0; y < height; y += step)
            {
                for (var x = 0; x < width; x += step)
                {
                    var i = (y * width + x) * 4;
                    colours.Add((pixels[i] >> 4) | (pixels[i + 1] >> 4 << 4) | (pixels[i + 2] >> 4 << 8));
                    if (colours.Count > 2) return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return true;
        }
    }

    private static string Describe(PageError error) =>
        error.Line > 0 ? $"{error.Message} ({Path.GetFileName(error.Source)}:{error.Line})" : error.Message;

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* WebView2 may still hold it */ }
    }
}

/// <summary>A dedicated STA thread with a WPF dispatcher, shut down on dispose.</summary>
internal sealed class StaHost : IDisposable
{
    private readonly Dispatcher _dispatcher;

    private StaHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static Task<StaHost> StartAsync(string name)
    {
        var started = new TaskCompletionSource<StaHost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            started.SetResult(new StaHost(dispatcher));
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task;
    }

    /// <summary>Runs async work on the STA thread and returns its result.</summary>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> work) =>
        await await _dispatcher.InvokeAsync(work).Task.ConfigureAwait(false);

    public async Task InvokeAsync(Func<Task> work) =>
        await await _dispatcher.InvokeAsync(work).Task.ConfigureAwait(false);

    public void Dispose() => _dispatcher.InvokeShutdown();
}
