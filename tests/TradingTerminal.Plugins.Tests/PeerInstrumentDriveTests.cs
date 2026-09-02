using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The drive serves a universe, not one instrument.
///
/// <para><b>The third instance of one defect, and it was found the same way as the other two — by
/// driving a real unit and asking why it did not clear.</b> A regime screen over an index compiled,
/// ran, and drew its warm-up message forever, so rung 7 reported <c>draw.text-only</c> against three
/// panels of a unit that was doing exactly what the brief asked. The view answered
/// <c>RecentBars</c> for one instrument, so there was nothing to rank.</para>
///
/// <para>That is the expensive direction: <c>AuthoringJudge</c> turns a rung failure into a repair
/// turn, so a false one spends a whole generation rewriting working code.</para>
/// </summary>
public sealed class PeerInstrumentDriveTests
{
    [Fact]
    public void ABasketUnitSeesMoreThanOneInstrument()
    {
        var unit = new BasketKernel();
        var drive = SyntheticDrive.Run(unit);

        drive.Instruments.Count.Should().BeGreaterThan(1);
        unit.Seen.Should().HaveCountGreaterThan(1, "a basket unit has nothing to compare against one");
        unit.Seen.Should().Contain(SyntheticDrive.Instrument);
    }

    [Fact]
    public void EveryPeerIsDeliveredThroughTheCallback()
    {
        // Delivered, not merely answerable. A view a unit can query but a drive never fills is the
        // same defect as a stream nobody publishes -- which is what the previous two instances were.
        var unit = new BasketKernel();
        SyntheticDrive.Run(unit);

        foreach (var peer in SyntheticDrive.Peers)
            unit.Seen.Should().Contain(peer, "peer {0} never arrived at OnBarAsync", peer.Value);
    }

    [Fact]
    public void APeerCanBeReadBackFromTheView()
    {
        var unit = new BasketKernel();
        SyntheticDrive.Run(unit);

        foreach (var peer in SyntheticDrive.Peers)
        {
            unit.History[peer].Should().NotBeEmpty(
                "RecentBars must answer for a peer the drive delivered");
        }
    }

    [Fact]
    public void ThePeersAreNotCopiesOfEachOther()
    {
        // A matrix whose rows are identical has every correlation at one and every ranking arbitrary,
        // which makes a unit that computes them wrongly draw the same picture as one that computes
        // them correctly. The synthetic book is lopsided for the same reason.
        var unit = new BasketKernel();
        SyntheticDrive.Run(unit);

        var lastCloses = SyntheticDrive.Universe
            .Select(id => unit.History[id][^1].Close)
            .ToArray();

        lastCloses.Distinct().Should().HaveCount(
            lastCloses.Length, "identical peers make a correct ranking indistinguishable from a broken one");
    }

    [Fact]
    public void HistoryGrowsRatherThanArrivingWhole()
    {
        // A unit that can read a hundred bars of a peer on the first bar of the session is reading a
        // future the live feed will not give it, and every warm-up guard would be untested.
        var unit = new BasketKernel();
        SyntheticDrive.Run(unit);

        unit.FirstPeerHistoryLength.Should().BeLessThan(
            unit.History[SyntheticDrive.Peers[0]].Count,
            "the peer's history should have grown across the drive rather than being complete at the start");
    }

    /// <summary>Records what it was given, for every instrument the drive serves.</summary>
    private sealed class BasketKernel : IStrategyKernel
    {
        public HashSet<InstrumentId> Seen { get; } = [];

        public Dictionary<InstrumentId, List<OhlcvBar>> History { get; } = [];

        public int FirstPeerHistoryLength { get; private set; } = -1;

        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)));

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

        public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.Parameters.GetInstrument("instrument");
            return Task.CompletedTask;
        }

        public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(bar);
            ArgumentNullException.ThrowIfNull(context);

            Seen.Add(bar.InstrumentId);

            if (!History.TryGetValue(bar.InstrumentId, out var bars))
                History[bar.InstrumentId] = bars = [];
            bars.Add(bar);

            if (FirstPeerHistoryLength < 0 && bar.InstrumentId == SyntheticDrive.Peers[0])
            {
                FirstPeerHistoryLength = context.Data
                    .RecentBars(bar.InstrumentId, BarSize.OneMinute, 500).Count;
            }

            return Task.CompletedTask;
        }
    }
}
