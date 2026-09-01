namespace DaxAlgo.Sdk;

/// <summary>
/// A render surface that discards everything.
///
/// <para>This is the correct surface for a host with nothing on screen — a headless run, a test, a
/// visualizer executing while its panel is closed. A visualizer should never have to ask whether it
/// is visible: it describes the frame, and when nobody is looking the description goes nowhere.</para>
///
/// <para><see cref="Viewport"/> reports zero size and <see cref="Cursor"/> reports outside, so a
/// visualizer that scales to the viewport degrades to drawing nothing rather than dividing by a
/// bogus width.</para>
/// </summary>
public sealed class NullRenderSurface : IRenderSurface
{
    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>The shared instance. It holds no state, so one is enough.</summary>
    public static NullRenderSurface Instance { get; } = new();

    public RenderViewport Viewport => new(0d, 0d, 1d);

    public RenderCursor Cursor => new(0d, 0d, IsInside: false, IsPressed: false);

    /// <summary>No clock, so no time. A unit that animates draws its first frame, which is what it
    /// must look sensible at anyway.</summary>
    public DateTime Now => DateTime.MinValue;

    public RenderColor Theme(RenderThemeColor token) => new(0, 0, 0);

    public void SetStyle(RenderStyle style)
    {
    }

    public IDisposable Panel(string title, RenderPanelKind kind) => NoScope.Instance;

    public void AxisX(double minimum, double maximum, string? format = null)
    {
    }

    public void AxisY(double minimum, double maximum, string? format = null)
    {
    }

    public IDisposable Series(string name, RenderSeriesKind kind) => NoScope.Instance;

    public void Push(double x, double y)
    {
    }

    public void Line(double x1, double y1, double x2, double y2)
    {
    }

    public void Rect(double x, double y, double width, double height, bool filled = true)
    {
    }

    public void Text(double x, double y, string text)
    {
    }

    public void Marker(double x, double y, RenderMarkerShape shape)
    {
    }
}
