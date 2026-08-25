using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// A live, drawable instance of a freshly compiled unit — so an author sees the <b>picture</b> rather
/// than the code that would have produced it.
///
/// <para>Reading generated C# to find out what a strategy will look like is a poor way to review one.
/// Most people cannot, and the ones who can still cannot tell from the source whether the axes are
/// sensible, whether the bands are where they should be, or whether it drew anything at all. The whole
/// point of the render contract is that a picture can be produced without a window; this is that
/// capability pointed at the authoring pane.</para>
///
/// <para>It is the same instance the verifier drove, over the same series, so what the preview shows and
/// what the ladder judged cannot disagree.</para>
/// </summary>
public sealed class AuthoredUnitPreview
{
    private AuthoredUnitPreview(
        Action<IRenderSurface>? draw,
        AuthoringKind kind,
        string summary,
        bool isDrawable)
    {
        Draw = draw;
        Kind = kind;
        Summary = summary;
        IsDrawable = isDrawable;
    }

    /// <summary>The frame callback to hand to a render surface, or null when there is nothing to show.</summary>
    public Action<IRenderSurface>? Draw { get; }

    public AuthoringKind Kind { get; }

    /// <summary>One line for the pane: what is on screen, or why nothing is.</summary>
    public string Summary { get; }

    /// <summary>False when the unit cannot be previewed at all — the pane should show the reason rather
    /// than an empty rectangle a user would read as a broken application.</summary>
    public bool IsDrawable { get; }

    /// <summary>What the unit did to its book during the preview drive, for the summary line.</summary>
    public int TargetsSubmitted { get; private init; }

    /// <summary>
    /// Compiles nothing and assumes nothing: takes the type the compiler already resolved, constructs
    /// it, drives it, and hands back its <c>Draw</c>.
    /// </summary>
    public static AuthoredUnitPreview Create(AuthoredUnit unit, double[]? closes = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (unit.UsesRetiredContract)
        {
            return Unavailable(
                unit.Kind,
                $"'{unit.Type.Name}' uses {unit.ContractName}, which cannot be driven or drawn. "
                + "Implement IStrategyKernel to get a preview.");
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(unit.Type)
                ?? throw new InvalidOperationException("Activator returned null.");
        }
        catch (Exception ex)
        {
            var cause = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? inner
                : ex;
            return Unavailable(unit.Kind, $"Could not construct it: {cause.Message}");
        }

        try
        {
            return instance switch
            {
                IStrategyKernel kernel => Drive(kernel, closes),
                IVisualizer visualizer => Drive(visualizer, closes),
                _ => Unavailable(unit.Kind, "It implements neither IStrategyKernel nor IVisualizer."),
            };
        }
        catch (Exception ex)
        {
            // A throw during the drive is a real fault the ladder also reports; here it just means there
            // is no picture, and saying which exception beats an empty panel.
            return Unavailable(unit.Kind, $"It threw while running: {ex.GetType().Name} — {ex.Message}");
        }
    }

    private static AuthoredUnitPreview Drive(IStrategyKernel kernel, double[]? closes)
    {
        var result = SyntheticDrive.Run(kernel, closes);
        var targets = result.Book.Intents.Count;

        // A strategy that draws nothing is legitimate, and the pane should say so rather than show a
        // blank rectangle that reads as a failure.
        var probe = new RecordingRenderSurface();
        kernel.Draw(probe);

        return probe.IsBlank
            ? new AuthoredUnitPreview(
                null,
                AuthoringKind.Strategy,
                targets == 0
                    ? "Runs, draws nothing, and took no position on the preview data."
                    : $"Runs and took {Plural(targets)}, but draws nothing — a signal-only strategy.",
                isDrawable: false) { TargetsSubmitted = targets }
            : new AuthoredUnitPreview(
                kernel.Draw,
                AuthoringKind.Strategy,
                targets == 0
                    ? "Live preview. No position taken on the preview data."
                    : $"Live preview. Took {Plural(targets)}.",
                isDrawable: true) { TargetsSubmitted = targets };
    }

    private static AuthoredUnitPreview Drive(IVisualizer visualizer, double[]? closes)
    {
        SyntheticDrive.Run(visualizer, closes);

        var probe = new RecordingRenderSurface();
        visualizer.Draw(probe);

        return probe.IsBlank
            ? Unavailable(
                AuthoringKind.Visualizer,
                "It runs but paints nothing. A visualizer that draws nothing has no other purpose.")
            : new AuthoredUnitPreview(visualizer.Draw, AuthoringKind.Visualizer, "Live preview.", true);
    }

    private static AuthoredUnitPreview Unavailable(AuthoringKind kind, string reason) =>
        new(null, kind, reason, isDrawable: false);

    private static string Plural(int count) => count == 1 ? "1 position" : $"{count} positions";
}
