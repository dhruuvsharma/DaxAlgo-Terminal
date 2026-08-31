using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI.Logging;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The host that drives an open unit's window: it paces the frames, fills the parameter list, and
/// shows the unit's own lines out of the app-wide activity log.
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class AuthoredUnitHostTests : IDisposable
{
    private readonly Func<TimeSpan, Action, IDisposable> _realTimer = UiThread.CreateRenderTimer;
    private ManualTimer? _timer;

    public AuthoredUnitHostTests() =>
        UiThread.CreateRenderTimer = (interval, tick) => _timer = new ManualTimer(interval, tick);

    public void Dispose() => UiThread.CreateRenderTimer = _realTimer;

    [Fact]
    public void FramesArePacedRatherThanPushed()
    {
        // Market data arrives far faster than a display can show it. Redrawing per event would burn
        // the UI thread producing frames nobody sees, so the timer decides when to ask.
        using var host = Arrange(out var drawn);
        var frames = 0;
        host.Presenter.FrameRequested += (_, _) => frames++;

        Assert.Equal(0, frames);
        _timer!.Fire();
        _timer.Fire();

        Assert.Equal(2, frames);
        // Asking for a frame is not drawing one — the view decides that, and does it lazily.
        Assert.Empty(drawn);
    }

    [Fact]
    public void TheHostDrawsThroughWhateverIsRunningTheUnit()
    {
        using var host = Arrange(out var drawn);

        host.Presenter.Draw!(new NullRenderSurface());

        Assert.Single(drawn);
    }

    [Fact]
    public void FreezingStopsTheFramesAndLeavesTheLastPictureOnScreen()
    {
        // A unit that stops should not make its window go blank: the last frame is still the most
        // useful thing on screen, and it is usually what the user wants to look at afterwards.
        using var host = Arrange(out _);
        var frames = 0;
        host.Presenter.FrameRequested += (_, _) => frames++;

        host.Freeze();
        _timer!.Fire();

        Assert.Equal(0, frames);
        Assert.True(_timer.IsDisposed);
        Assert.NotNull(host.Presenter.Draw);
    }

    [Fact]
    public void DisposingStopsTheTimer()
    {
        // A window left open all day must not keep a timer alive after it closes.
        var host = Arrange(out _);

        host.Dispose();

        Assert.True(_timer!.IsDisposed);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var host = Arrange(out _);

        host.Dispose();
        var fault = Record.Exception(host.Dispose);

        Assert.Null(fault);
    }

    [Fact]
    public void ParametersAreShownWithTheValuesInForce()
    {
        var schema = new StrategyParameterSchema(
            new StrategyParameter { Key = "levels", DisplayName = "Depth levels", Default = 10 },
            new StrategyParameter { Key = "smooth", DisplayName = "Smoothing", Default = 0.25d },
            new StrategyParameter { Key = "poc", DisplayName = "Show POC", Default = true });

        using var host = new AuthoredUnitHost(
            "Book",
            _ => true,
            schema,
            values: new Dictionary<string, object?> { ["levels"] = 25 });

        Assert.Equal(["Depth levels", "Smoothing", "Show POC"], host.Presenter.Parameters.Select(p => p.Label));

        // The supplied value wins; the others fall back to what the unit declared.
        //
        // "true" rather than the prettier "on", and "0.25" invariantly rather than in the current
        // culture, because this text is no longer display-only: an editable row parses it back on
        // apply. A value formatted for reading and parsed for meaning is how a decimal comma turns
        // 0.25 into 25.
        Assert.Equal(["25", "0.25", "true"], host.Presenter.Parameters.Select(p => p.Value));

        // Seeded, so nothing is pending before the user has touched anything.
        Assert.All(host.Presenter.Parameters, p => Assert.False(p.IsDirty));
    }

    [Fact]
    public void TheParameterExpanderStartsClosedOnceAUnitIsRunning()
    {
        // The picture is the point. The parameters are reference material at this stage.
        using var host = Arrange(out _);

        Assert.False(host.Presenter.IsSetupExpanded);
    }

    [Fact]
    public void TheWindowShowsThisUnitsLinesAndNobodyElses()
    {
        // There is exactly ONE activity log in the application; this is a filtered view of it, not a
        // second log. A private one would drift from the main pane and answer the same question twice.
        var log = new InMemoryLogSink();
        using var host = new AuthoredUnitHost("Book", _ => true, log: log);

        log.Append("Book", "Information", "mine");
        log.Append("System", "Information", "not mine");
        log.Append("Other Unit", "Warning", "also not mine");

        Assert.Equal(["mine"], host.Presenter.Log.Select(line => line.Message));
    }

    [Fact]
    public void LinesLoggedBeforeTheWindowOpenedAreStillShown()
    {
        // A unit that failed to start logged the reason before anyone could open its window, and that
        // line is the single most useful thing the window can show.
        var log = new InMemoryLogSink();
        log.Append("Book", "Error", "the feed refused depth");

        using var host = new AuthoredUnitHost("Book", _ => true, log: log);

        Assert.Equal(["the feed refused depth"], host.Presenter.Log.Select(line => line.Message));
    }

    [Fact]
    public void ADisposedHostStopsMirroringTheLog()
    {
        var log = new InMemoryLogSink();
        var host = new AuthoredUnitHost("Book", _ => true, log: log);

        host.Dispose();
        log.Append("Book", "Information", "after close");

        Assert.Empty(host.Presenter.Log);
    }

    [Fact]
    public void AStrategyShowsTheBookAndAVisualizerDoesNot()
    {
        using var strategy = new AuthoredUnitHost("S", _ => true, hasBook: true);
        using var visualizer = new AuthoredUnitHost("V", _ => true);

        Assert.True(strategy.Presenter.HasBook);
        Assert.False(visualizer.Presenter.HasBook);
    }

    private AuthoredUnitHost Arrange(out List<IRenderSurface> drawn)
    {
        var surfaces = new List<IRenderSurface>();
        drawn = surfaces;
        return new AuthoredUnitHost(
            "Book",
            surface =>
            {
                surfaces.Add(surface);
                return true;
            });
    }

    /// <summary>A render timer the test drives, so frame pacing is asserted rather than waited on.</summary>
    private sealed class ManualTimer(TimeSpan interval, Action tick) : IDisposable
    {
        internal TimeSpan Interval { get; } = interval;

        internal bool IsDisposed { get; private set; }

        internal void Fire()
        {
            if (!IsDisposed)
                tick();
        }

        public void Dispose() => IsDisposed = true;
    }
}
