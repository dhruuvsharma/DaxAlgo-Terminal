using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Runs rungs 5 to 8 against a compiled unit: instantiate it, drive it, read what it did.
///
/// <para>This is what replaced <c>StrategyBacktestSmoke</c>, which drove forty-eight fabricated ticks
/// past a stub clock and a stub router and reported its verdict as advice. Two things were wrong with
/// it. It needed the engine-era registration type, so for a unit written against the contracts the
/// guidance now teaches it silently did nothing at all. And a strategy could not fail against those
/// stubs in any way that mattered, because the only thing it could do was place an order into
/// nothing.</para>
///
/// <para>Here the unit trades its own book and draws its own frame, and both are read back.</para>
/// </summary>
public static class AuthoredUnitVerifier
{
    /// <summary>Instantiates <paramref name="unit"/> and runs the ladder from rung 5 up.</summary>
    /// <param name="unit">The type resolved by the compiler.</param>
    /// <param name="closes">Optional price series; the default is deliberately awkward.</param>
    public static VerificationReport Verify(AuthoredUnit unit, double[]? closes = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        // A unit on the retired contract cannot be driven through the sandbox at all — it wants an
        // order router. Saying so beats reporting a pass nothing earned.
        if (unit.UsesRetiredContract)
        {
            return new VerificationReport(
            [
                VerificationStep.Fail(
                    VerificationRung.Lifecycle,
                    new VerificationFinding(
                        "lifecycle.retired-contract",
                        $"'{unit.Type.Name}' implements {unit.ContractName}, the archived engine's "
                        + "contract, and cannot be driven through the sandbox.",
                        "Implement IStrategyKernel and submit position targets through context.Book "
                        + "instead of placing orders through a router.")),
            ]);
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(unit.Type)
                ?? throw new InvalidOperationException("Activator returned null.");
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } cause }
                ? cause
                : ex;
            return new VerificationReport(
            [
                VerificationStep.Fail(
                    VerificationRung.Lifecycle,
                    new VerificationFinding(
                        "lifecycle.construction-failed",
                        $"Could not construct '{unit.Type.Name}': {inner.Message}",
                        "The host creates the unit with a public parameterless constructor and passes "
                        + "everything else through OnStartAsync. Move set-up there.")),
            ]);
        }

        return instance switch
        {
            IStrategyKernel kernel => VerifyKernel(kernel, closes),
            IVisualizer visualizer => VerifyVisualizer(visualizer, closes),
            _ => new VerificationReport(
            [
                VerificationStep.Fail(
                    VerificationRung.Shape,
                    new VerificationFinding(
                        "shape.not-hostable",
                        $"'{unit.Type.Name}' implements neither IStrategyKernel nor IVisualizer.",
                        "A strategy implements IStrategyKernel; a visualizer implements IVisualizer.")),
            ]),
        };
    }

    private static VerificationReport VerifyKernel(IStrategyKernel kernel, double[]? closes)
    {
        SyntheticDrive.Result? drive = null;

        return LadderRunner.RunGuarded(
            (VerificationRung.Lifecycle, () => LifecycleProbe.Run(
                () => drive = SyntheticDrive.Run(kernel, closes),
                phase: "the strategy lifecycle")),

            (VerificationRung.SchemaCoherence, () => SchemaCoherenceProbe.Run(
                kernel.Schema,
                drive?.Parameters.KeysRead ?? [],
                drivenToCompletion: drive?.Completed == true)),

            // A strategy may legitimately draw nothing, so mustDraw is false — but if it drew only a
            // warm-up message after fourteen bars, it is still explaining itself when it should be
            // showing something.
            //
            // Through the LAYOUT when it declares one, because that is the picture the host builds; the
            // strategy exemplar declares a two-panel window, so this is not a rare shape.
            (VerificationRung.DrawProbe, () => DrawProbe.RunLayout(
                kernel.Layout, kernel.Draw, mustDraw: false, requirePicture: true)),

            (VerificationRung.Replay, () => ReplayProbe.Run(
                drive?.Book.Intents ?? [],
                drive?.Instruments ?? new HashSet<Core.Domain.InstrumentId>())));
    }

    private static VerificationReport VerifyVisualizer(IVisualizer visualizer, double[]? closes)
    {
        SyntheticDrive.Result? drive = null;

        return LadderRunner.RunGuarded(
            (VerificationRung.Lifecycle, () => LifecycleProbe.Run(
                () => drive = SyntheticDrive.Run(visualizer, closes),
                phase: "the visualizer lifecycle")),

            (VerificationRung.SchemaCoherence, () => SchemaCoherenceProbe.Run(
                visualizer.Schema,
                drive?.Parameters.KeysRead ?? [],
                drivenToCompletion: drive?.Completed == true)),

            // A visualizer that draws nothing has no other purpose, so this one is not optional — and it
            // is asked of the PANELS when the unit declares a layout, since those are what the host
            // renders and `Draw` is documented as unused once one exists.
            (VerificationRung.DrawProbe, () => DrawProbe.RunLayout(
                visualizer.Layout, visualizer.Draw, mustDraw: true, requirePicture: true)),

            // Nothing to replay: a visualizer has no book. Skipped rather than passed, so it earns
            // nothing for a rung it never faced.
            (VerificationRung.Replay, () => VerificationStep.Skip(VerificationRung.Replay)));
    }
}
