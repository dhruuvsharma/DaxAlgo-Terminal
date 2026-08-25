using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Rung 8 of the verification ladder (#46) — what the strategy did to its book.
///
/// <para>The faults here all survive every earlier rung. Code that compiles, starts, draws and then
/// does something economically incoherent passes rungs 1 through 7 without a murmur, and only shows
/// itself when money is involved.</para>
/// </summary>
public sealed class ReplayProbeTests
{
    private static readonly InstrumentId Traded = new(4242);

    private static readonly IReadOnlySet<InstrumentId> Declared = new HashSet<InstrumentId> { Traded };

    private static VirtualTargetIntent Intent(
        double units,
        double? entry = null,
        double? stop = null,
        double? target = null,
        InstrumentId? instrument = null) =>
        new(
            instrument ?? Traded,
            units,
            stop,
            target,
            entry is null ? VirtualEntryKind.Market : VirtualEntryKind.Limit,
            entry);

    [Fact]
    public void ACoherentSetOfTargetsPasses()
    {
        ReplayProbe.Run(
            [Intent(1d, entry: 100d, stop: 98d, target: 105d), Intent(0d)],
            Declared).Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void AStrategyThatNeverTradedIsSkippedRatherThanFailed()
    {
        // Staying out of a window with no setup is correct behaviour. Punishing it would teach a model
        // to trade for the sake of trading, which is the worst habit a reward signal could instil.
        ReplayProbe.Run([], Declared).Outcome.Should().Be(VerificationOutcome.NotApplicable);
    }

    // ── The sign errors ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALongWithItsStopAboveTheEntryFails()
    {
        // A one-character sign error that guarantees the loss the stop exists to prevent, and is
        // completely invisible until it costs money.
        var step = ReplayProbe.Run([Intent(1d, entry: 100d, stop: 102d)], Declared);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        var finding = step.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("replay.stop-wrong-side");
        finding.Message.Should().Contain("ABOVE");
    }

    [Fact]
    public void AShortWithItsStopBelowTheEntryFails()
    {
        ReplayProbe.Run([Intent(-1d, entry: 100d, stop: 98d)], Declared)
            .Findings.Should().ContainSingle().Which.Code.Should().Be("replay.stop-wrong-side");
    }

    [Fact]
    public void AProfitTargetOnTheWrongSideFails()
    {
        ReplayProbe.Run([Intent(1d, entry: 100d, target: 95d)], Declared)
            .Findings.Should().ContainSingle().Which.Code.Should().Be("replay.target-wrong-side");
    }

    [Fact]
    public void AMarketEntryIsNotJudgedAgainstAPriceItNeverStated()
    {
        // Without a stated entry there is no price for the stop to be wrong about, and inventing one
        // from market data would produce exactly the confident-but-wrong diagnostic this ladder exists
        // to avoid emitting.
        ReplayProbe.Run([Intent(1d, stop: 102d)], Declared)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void AFlatTargetIsNotJudgedForItsStopSide()
    {
        // Going flat with protection attached is odd but not incoherent, and there is no position for
        // the stop to be on the wrong side of.
        ReplayProbe.Run([Intent(0d, entry: 100d, stop: 102d)], Declared)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    // ── The declarative-book misreading ─────────────────────────────────────────────────────────

    [Fact]
    public void APositionThatWalksAwayIsCaught()
    {
        // The classic misreading: treating a declarative book as an order API, so every bar ADDS a unit
        // instead of restating the target. It compiles, it runs, and it ends up a hundred long.
        var walking = Enumerable.Range(1, 150).Select(units => Intent(units)).ToArray();

        var step = ReplayProbe.Run(walking, Declared);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Code.Should().Be("replay.runaway-position");
        step.Findings[0].Remedy.Should().Contain("does not add");
    }

    [Fact]
    public void ANonFiniteTargetIsCaught()
    {
        ReplayProbe.Run([Intent(0d / 0d)], Declared)
            .Findings.Should().ContainSingle().Which.Code.Should().Be("replay.non-finite-target");
    }

    [Fact]
    public void TradingAnInstrumentThatWasNeverDeclaredIsCaught()
    {
        ReplayProbe.Run([Intent(1d, instrument: new InstrumentId(9999))], Declared)
            .Findings.Should().ContainSingle().Which.Code.Should().Be("replay.undeclared-instrument");
    }

    [Fact]
    public void OnlyTheFirstFaultIsReported()
    {
        // Later intents are usually consequences of the first mistake, and a repair agent handed six
        // findings for one bug fixes the wrong one first.
        var step = ReplayProbe.Run(
            [Intent(1d, entry: 100d, stop: 102d), Intent(0d / 0d), Intent(1d, instrument: new InstrumentId(1))],
            Declared);

        step.Findings.Should().ContainSingle();
    }

    [Fact]
    public void EveryFindingCarriesARemedy()
    {
        VirtualTargetIntent[][] bad =
        [
            [Intent(1d, entry: 100d, stop: 102d)],
            [Intent(0d / 0d)],
            [Intent(1d, instrument: new InstrumentId(9999))],
            [Intent(500d)],
        ];

        foreach (var intents in bad)
        {
            foreach (var finding in ReplayProbe.Run(intents, Declared).Findings)
            {
                finding.Remedy.Should().NotBeNullOrWhiteSpace($"'{finding.Code}' must say what to change");
                finding.Code.Should().StartWith("replay.").And.NotContain(" ");
            }
        }
    }
}
