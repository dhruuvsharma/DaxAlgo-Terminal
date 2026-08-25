using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Rung 8 — what the strategy did to its book once real data went through it.
///
/// <para>The deepest signal on the ladder, and the cheapest deep signal anyone gets: because a strategy's
/// only output is its own virtual book, replaying data and reading the book back <b>is</b> the complete
/// record of its behaviour. There is no router to stub, no fill to simulate and no account to fake.</para>
///
/// <para>What it checks is the class of fault that survives every earlier rung: code that compiles,
/// starts, draws and then does something economically incoherent. A protective stop on the wrong side of
/// the position is the clearest example — an instant loss, a one-character sign error, and completely
/// invisible until money is involved.</para>
/// </summary>
public static class ReplayProbe
{
    /// <param name="intents">Everything the strategy submitted, from <see cref="RecordingVirtualBook"/>.</param>
    /// <param name="declaredInstruments">The instrument set the context authorised.</param>
    /// <param name="maximumUnits">Largest absolute position considered sane for a verification run. A
    /// strategy that walks past this is almost always accumulating rather than targeting — the classic
    /// misreading of a declarative book as an order API, where every bar adds instead of restating.</param>
    public static VerificationStep Run(
        IReadOnlyList<VirtualTargetIntent> intents,
        IReadOnlySet<InstrumentId> declaredInstruments,
        double maximumUnits = 100d)
    {
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(declaredInstruments);

        // No targets is not a failure. A strategy that saw no setup in the replay window did the right
        // thing by staying out, and punishing that teaches a model to trade for the sake of trading —
        // which is the single worst habit it could learn from a reward signal.
        if (intents.Count == 0) return VerificationStep.Skip(VerificationRung.Replay);

        var findings = new List<VerificationFinding>();

        foreach (var intent in intents)
        {
            if (!double.IsFinite(intent.TargetUnits))
            {
                findings.Add(new VerificationFinding(
                    "replay.non-finite-target",
                    $"Submitted a target of {intent.TargetUnits}.",
                    "A target must be a finite number of units. This is usually a division by a count "
                    + "that was still zero during warm-up."));
                break;
            }

            if (!declaredInstruments.Contains(intent.Instrument))
            {
                findings.Add(new VerificationFinding(
                    "replay.undeclared-instrument",
                    $"Submitted a target for instrument {intent.Instrument} which is not in the "
                    + "context's declared set.",
                    "Trade only instruments the parameters selected. The host rejects the rest at "
                    + "runtime."));
                break;
            }

            if (Math.Abs(intent.TargetUnits) > maximumUnits)
            {
                findings.Add(new VerificationFinding(
                    "replay.runaway-position",
                    $"Reached a target of {intent.TargetUnits:0.##} units.",
                    "The book is declarative: SetTargetPosition states the position you WANT, it does "
                    + "not add to the one you have. Restate the target rather than accumulating."));
                break;
            }

            if (StopIsOnTheWrongSide(intent, out var detail))
            {
                findings.Add(new VerificationFinding(
                    "replay.stop-wrong-side",
                    detail,
                    "A protective stop goes below a long entry and above a short one. Reversing it "
                    + "guarantees the loss it exists to prevent."));
                break;
            }

            if (TargetIsOnTheWrongSide(intent, out var targetDetail))
            {
                findings.Add(new VerificationFinding(
                    "replay.target-wrong-side",
                    targetDetail,
                    "A profit target goes above a long entry and below a short one."));
                break;
            }
        }

        return findings.Count == 0
            ? VerificationStep.Pass(VerificationRung.Replay)
            : new VerificationStep(VerificationRung.Replay, VerificationOutcome.Failed, findings);
    }

    /// <summary>
    /// A stop is only checkable against a stated entry price, so this compares against
    /// <see cref="VirtualTargetIntent.EntryTriggerPrice"/> when there is one. A market entry has no
    /// price to be wrong about here, and guessing one from market data would produce exactly the
    /// confident-but-wrong diagnostic this ladder exists to avoid emitting.
    /// </summary>
    private static bool StopIsOnTheWrongSide(VirtualTargetIntent intent, out string detail)
    {
        detail = string.Empty;
        if (intent.ProtectiveStopPrice is not { } stop ||
            intent.EntryTriggerPrice is not { } entry ||
            intent.TargetUnits == 0d)
        {
            return false;
        }

        var isLong = intent.TargetUnits > 0d;
        if (isLong && stop < entry) return false;
        if (!isLong && stop > entry) return false;

        detail = isLong
            ? $"Long {intent.TargetUnits:0.##} entering at {entry:0.####} with a protective stop ABOVE it at {stop:0.####}."
            : $"Short {intent.TargetUnits:0.##} entering at {entry:0.####} with a protective stop BELOW it at {stop:0.####}.";
        return true;
    }

    private static bool TargetIsOnTheWrongSide(VirtualTargetIntent intent, out string detail)
    {
        detail = string.Empty;
        if (intent.ProfitTargetPrice is not { } target ||
            intent.EntryTriggerPrice is not { } entry ||
            intent.TargetUnits == 0d)
        {
            return false;
        }

        var isLong = intent.TargetUnits > 0d;
        if (isLong && target > entry) return false;
        if (!isLong && target < entry) return false;

        detail = isLong
            ? $"Long {intent.TargetUnits:0.##} entering at {entry:0.####} with a profit target BELOW it at {target:0.####}."
            : $"Short {intent.TargetUnits:0.##} entering at {entry:0.####} with a profit target ABOVE it at {target:0.####}.";
        return true;
    }
}
