using DaxAlgo.Sdk;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App;

/// <summary>
/// Builds the pair that IS an open authored-visualizer window: the sandbox runtime that owns the unit,
/// and the host that owns the chrome around it.
///
/// <para><b>Extracted from the shell because it was the one place nothing could test.</b> This wiring
/// lived inside a lambda inside <c>OpenVisualizer</c>, behind a DI container, a window and an overlay,
/// so the only way to find a missing seam was to open the terminal and look. That is how the verb
/// capability shipped with the buttons built, bound, and never passed: <c>AuthoredUnitHost</c> took
/// <c>actions</c> and <c>invokeAction</c>, this call site passed neither, and every unit test
/// constructed the host directly rather than the way the application does.</para>
///
/// <para>A plain static method taking exactly what it needs, so a test can call it and assert on every
/// seam. Adding a seam to <see cref="AuthoredUnitHost"/> and forgetting it here now fails a test
/// instead of shipping.</para>
/// </summary>
internal static class AuthoredVisualizerComposition
{
    /// <summary>The runtime and the window host, wired to each other.</summary>
    /// <param name="name">Display name, used for the window title and the log source.</param>
    /// <param name="createVisualizer">Builds a fresh unit — the runtime calls it per session.</param>
    /// <param name="schema">Read off a throwaway instance, because asking the running one would mean
    /// reaching through the gate that keeps the pump and the render thread apart.</param>
    internal static (SandboxVisualizerRuntime Runtime, AuthoredUnitHost Unit) Create(
        string name,
        Func<IVisualizer> createVisualizer,
        StrategyParameterSchema schema,
        IMarketDataHub hub,
        IClock clock,
        InMemoryLogSink log)
    {
        ArgumentNullException.ThrowIfNull(createVisualizer);
        ArgumentNullException.ThrowIfNull(log);

        // Declared before the runtime so the take-away callback can reach it: the runtime is built
        // first and the window second, and an offer has to land on the window.
        AuthoredUnitHost? unit = null;

        var runtime = new SandboxVisualizerRuntime(
            createVisualizer,
            currentValues: null,
            hub,
            clock,
            log.Append,
            alert => log.Append(alert.Source, alert.Level.ToString(), alert.Message),
            offerTakeAway: (label, text) => unit?.TakeAway(label, text));

        // The apply and pause seams. The runtime already supports the exact flow an editable parameter
        // needs — pause, set, resume, which rebuilds the session from the new values — so this is a
        // wiring job rather than a lifecycle of its own. Passing them is also what makes the rows
        // editable at all: a host that supplies neither gets the read-only window.
        unit = new AuthoredUnitHost(
            name, runtime.TryDraw, schema, values: null, log,
            apply: async values =>
            {
                if (runtime.IsRunning) await runtime.PauseAsync();
                foreach (var (key, value) in values) runtime.SetParameter(key, value);
                await runtime.ResumeAsync();
            },
            setPaused: async pause =>
            {
                if (pause) await runtime.PauseAsync();
                else await runtime.ResumeAsync();
            },

            // Until this was passed, a unit could DECLARE a multi-panel window, have it validated, see
            // it in the preview — and then open as one panel, because nothing ever asked the running
            // unit for its layout.
            layout: runtime.GetLayout,

            // The verbs a unit declares, and the way to run one. Without BOTH the buttons are built,
            // bound and never shown.
            actions: () => runtime.Actions,
            invokeAction: id => runtime.InvokeActionAsync(id));

        return (runtime, unit);
    }

    /// <summary>
    /// Starts the pair, in the order that makes it work.
    ///
    /// <para>The refresh afterwards is not optional and is not decoration. A unit does not exist until
    /// the runtime builds one, so the verbs read while composing are always none — which is the second
    /// way this capability managed to show no buttons after the seams were finally passed. Owning the
    /// start here rather than leaving it to each caller is what keeps that ordering from being
    /// something to remember.</para>
    /// </summary>
    internal static async Task StartAsync(
        SandboxVisualizerRuntime runtime, AuthoredUnitHost unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(unit);

        await runtime.StartAsync(ct).ConfigureAwait(true);
        unit.RefreshActions();
    }
}
