using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class FootprintForecastProviderTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    public void CoordinateAndRequestSupportSecondIntervals(int seconds)
    {
        var interval = TimeSpan.FromSeconds(seconds);
        var coordinate = Coordinate(interval);
        var history = new[] { Bar(0, interval), Bar(1, interval) };
        var request = new FootprintForecastRequest(coordinate, history, history[^1].EndUtc, 2);

        coordinate.InstrumentKey.Should().Be("ESM6");
        coordinate.Source.Should().Be(BrokerKind.Simulated);
        coordinate.Interval.Should().Be(interval);
        coordinate.RowSize.Should().Be(0.25);
        request.CutoffUtc.Should().Be(Epoch.Add(interval * 2));
    }

    [Fact]
    public void CoordinateRejectsInvalidValues()
    {
        var noInstrument = () => new FootprintForecastCoordinate(" ", BrokerKind.Simulated, TimeSpan.FromSeconds(30), 0.25);
        var source = () => new FootprintForecastCoordinate("ESM6", (BrokerKind)(-1), TimeSpan.FromSeconds(30), 0.25);
        var interval = () => new FootprintForecastCoordinate("ESM6", BrokerKind.Simulated, TimeSpan.Zero, 0.25);
        var rowSize = () => new FootprintForecastCoordinate("ESM6", BrokerKind.Simulated, TimeSpan.FromSeconds(30), double.NaN);

        noInstrument.Should().Throw<ArgumentException>();
        source.Should().Throw<ArgumentOutOfRangeException>();
        interval.Should().Throw<ArgumentOutOfRangeException>();
        rowSize.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RequestBoundsHistoryAndHorizonAndRequiresExactCutoff()
    {
        var coordinate = Coordinate();
        var bar = Bar(0);
        var noHistory = () => new FootprintForecastRequest(coordinate, Array.Empty<FootprintBar>(), Epoch, 1);
        var zeroHorizon = () => new FootprintForecastRequest(coordinate, new[] { bar }, bar.EndUtc, 0);
        var excessHorizon = () => new FootprintForecastRequest(
            coordinate, new[] { bar }, bar.EndUtc, FootprintForecastRequest.MaximumHorizonBars + 1);
        var wrongCutoff = () => new FootprintForecastRequest(coordinate, new[] { bar }, bar.StartUtc, 1);
        var excessHistory = () => new FootprintForecastRequest(
            coordinate,
            Enumerable.Range(0, FootprintForecastRequest.MaximumHistoryBars + 1).Select(static index => Bar(index)),
            Epoch.Add(TimeSpan.FromSeconds(30) * (FootprintForecastRequest.MaximumHistoryBars + 1)),
            1);

        noHistory.Should().Throw<ArgumentException>();
        zeroHorizon.Should().Throw<ArgumentOutOfRangeException>();
        excessHorizon.Should().Throw<ArgumentOutOfRangeException>();
        wrongCutoff.Should().Throw<ArgumentException>().WithMessage("*Cutoff*");
        excessHistory.Should().Throw<ArgumentException>().WithMessage("*exceed*");
    }

    [Fact]
    public void RequestSnapshotsCompleteBarsAndRows()
    {
        var rows = Rows();
        var source = new List<FootprintBar> { Bar(0, rows: rows) };
        var request = new FootprintForecastRequest(Coordinate(), source, source[^1].EndUtc, 1);

        source.Clear();
        rows[0] = Row(99, buy: 1, sell: 1);

        request.History.Should().ContainSingle();
        request.History[0].Rows.Should().HaveCount(3);
        request.History[0].Rows[0].Price.Should().Be(100.25);
        request.History[0].Rows[0].BuyVolume.Should().Be(10);
    }

    [Fact]
    public void RequestRequiresUtcExactDurationAndContiguousHistory()
    {
        var coordinate = Coordinate();
        var unspecified = Bar(0) with
        {
            StartUtc = DateTime.SpecifyKind(Epoch, DateTimeKind.Unspecified),
            EndUtc = DateTime.SpecifyKind(Epoch.AddSeconds(30), DateTimeKind.Unspecified),
        };
        var wrongDuration = Bar(0) with { EndUtc = Epoch.AddSeconds(31) };
        var gap = Bar(1) with
        {
            StartUtc = Epoch.AddSeconds(31),
            EndUtc = Epoch.AddSeconds(61),
        };
        var overlap = Bar(1) with
        {
            StartUtc = Epoch.AddSeconds(29),
            EndUtc = Epoch.AddSeconds(59),
        };

        var nonUtc = () => new FootprintForecastRequest(coordinate, new[] { unspecified }, unspecified.EndUtc, 1);
        var duration = () => new FootprintForecastRequest(coordinate, new[] { wrongDuration }, wrongDuration.EndUtc, 1);
        var gapped = () => new FootprintForecastRequest(coordinate, new[] { Bar(0), gap }, gap.EndUtc, 1);
        var overlapping = () => new FootprintForecastRequest(coordinate, new[] { Bar(0), overlap }, overlap.EndUtc, 1);

        nonUtc.Should().Throw<ArgumentException>().WithMessage("*UTC*");
        duration.Should().Throw<ArgumentException>().WithMessage("*duration*");
        gapped.Should().Throw<ArgumentException>().WithMessage("*contiguous*");
        overlapping.Should().Throw<ArgumentException>().WithMessage("*contiguous*");
    }

    [Fact]
    public void RequestRejectsIncompleteOrNonFiniteBarData()
    {
        var coordinate = Coordinate();
        var empty = Bar(0) with { Rows = Array.Empty<FootprintFeatureRow>() };
        var nonFinite = Bar(0) with
        {
            Rows = new[] { Row(double.NaN, 10, 5), Rows()[1], Rows()[2] },
        };
        var negativeVolume = Bar(0) with
        {
            Rows = new[] { Row(100.25, -1, 5), Rows()[1], Rows()[2] },
        };
        var inconsistentTotals = Bar(0) with { BuyVolume = 36 };

        Action Build(FootprintBar bar) => () =>
            new FootprintForecastRequest(coordinate, new[] { bar }, bar.EndUtc, 1);

        Build(empty).Should().Throw<ArgumentException>().WithMessage("*complete price rows*");
        Build(nonFinite).Should().Throw<ArgumentException>().WithMessage("*finite*");
        Build(negativeVolume).Should().Throw<ArgumentException>().WithMessage("*negative*");
        Build(inconsistentTotals).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void QuantilesRejectNonFiniteAndUnorderedValues()
    {
        var nonFinite = () => Q(double.NaN, 1, 2);
        var unordered = () => Q(2, 1, 3);

        nonFinite.Should().Throw<ArgumentOutOfRangeException>();
        unordered.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AvailableResultRequiresExactlyNSequentialTargetIntervals()
    {
        var request = Request(horizonBars: 3);
        var source = new List<FootprintHorizonForecast>
        {
            Forecast(request, 1),
            Forecast(request, 2),
            Forecast(request, 3),
        };

        var result = FootprintForecastResult.CreateAvailable(request, Model(), source);
        source.Clear();

        result.Status.Should().Be(FootprintForecastStatus.Available);
        result.Forecasts.Should().HaveCount(3);
        result.Forecasts.Select(static item => item.HorizonBars).Should().Equal(1, 2, 3);
        result.Forecasts[0].TargetStartUtc.Should().Be(request.CutoffUtc);
        result.Forecasts[2].TargetEndUtc.Should().Be(request.CutoffUtc.Add(request.Coordinate.Interval * 3));
    }

    [Fact]
    public void AvailableResultRejectsMissingExtraOrMisalignedHorizons()
    {
        var request = Request(horizonBars: 2);
        var missing = () => FootprintForecastResult.CreateAvailable(request, Model(), new[] { Forecast(request, 1) });
        var extra = () => FootprintForecastResult.CreateAvailable(
            request, Model(), new[] { Forecast(request, 1), Forecast(request, 2), Forecast(request, 3) });
        var wrongOrdinal = () => FootprintForecastResult.CreateAvailable(
            request, Model(), new[] { Forecast(request, 2), Forecast(request, 2) });
        var targetGap = () => FootprintForecastResult.CreateAvailable(
            request,
            Model(),
            new[]
            {
                Forecast(request, 1),
                Forecast(request, 2, targetOffset: request.Coordinate.Interval),
            });

        missing.Should().Throw<ArgumentException>().WithMessage("*exactly 2*");
        extra.Should().Throw<ArgumentException>().WithMessage("*exactly 2*");
        wrongOrdinal.Should().Throw<ArgumentException>().WithMessage("*sequential*");
        targetGap.Should().Throw<ArgumentException>().WithMessage("*contiguous*");
    }

    [Fact]
    public void HorizonForecastRejectsPocsOutsidePriceRange()
    {
        var request = Request();
        var lowAboveHigh = () => Forecast(
            request,
            1,
            low: Q(102, 103, 104),
            high: Q(101, 102, 103));
        var totalPocOutside = () => Forecast(request, 1, poc: Q(100, 101, 103));
        var buyPocOutside = () => Forecast(request, 1, buyPoc: Q(100.25, 100.75, 102.25));
        var sellPocOutside = () => Forecast(request, 1, sellPoc: Q(98.75, 100.25, 100.75));

        lowAboveHigh.Should().Throw<ArgumentException>();
        totalPocOutside.Should().Throw<ArgumentException>().WithMessage("*POC*");
        buyPocOutside.Should().Throw<ArgumentException>().WithMessage("*Buy POC*");
        sellPocOutside.Should().Throw<ArgumentException>().WithMessage("*Sell POC*");
    }

    [Fact]
    public void HorizonForecastEnforcesVolumeAndDeltaFractionBounds()
    {
        var request = Request();
        var negativeVolume = () => Forecast(request, 1, volume: Q(-1, 10, 20));
        var deltaBelowRange = () => Forecast(request, 1, deltaFraction: Q(-1.01, 0, 0.5));
        var deltaAboveRange = () => Forecast(request, 1, deltaFraction: Q(-0.5, 0, 1.01));

        negativeVolume.Should().Throw<ArgumentException>().WithMessage("*Volume*");
        deltaBelowRange.Should().Throw<ArgumentException>().WithMessage("*[-1, 1]*");
        deltaAboveRange.Should().Throw<ArgumentException>().WithMessage("*[-1, 1]*");
    }

    [Fact]
    public void DeltaMedianIsOptionalAndOnlyMultipliesTheTwoMedians()
    {
        var request = Request();
        var omitted = Forecast(request, 1);
        var included = Forecast(request, 1, includeDeltaMedian: true);

        omitted.DeltaMedian.Should().BeNull();
        included.DeltaMedian.Should().Be(30, "Q50(volume) 120 multiplied by Q50(delta fraction) 0.25");
        included.DeltaFraction.Should().Be(Q(-0.2, 0.25, 0.6));
    }

    [Fact]
    public async Task NullProviderCompletesWithoutWorkAndReturnsNoForecastBatch()
    {
        var request = Request(horizonBars: 3);
        var cancellation = new CancellationToken(canceled: true);
        var pending = new NullFootprintForecastProvider().ForecastAsync(request, cancellation);

        pending.IsCompletedSuccessfully.Should().BeTrue();
        var result = await pending;

        result.Status.Should().Be(FootprintForecastStatus.Unavailable);
        result.Model.Should().BeNull();
        result.Forecasts.Should().BeEmpty();
        result.CutoffUtc.Should().Be(request.CutoffUtc);
        result.HorizonBars.Should().Be(3);
    }

    private static FootprintForecastCoordinate Coordinate(TimeSpan? interval = null) =>
        new("ESM6", BrokerKind.Simulated, interval ?? TimeSpan.FromSeconds(30), rowSize: 0.25);

    private static FootprintForecastRequest Request(int horizonBars = 1)
    {
        var coordinate = Coordinate();
        var history = new[] { Bar(0), Bar(1) };
        return new FootprintForecastRequest(coordinate, history, history[^1].EndUtc, horizonBars);
    }

    private static FootprintBar Bar(
        int index,
        TimeSpan? interval = null,
        IReadOnlyList<FootprintFeatureRow>? rows = null)
    {
        var span = interval ?? TimeSpan.FromSeconds(30);
        rows ??= Rows();
        return new FootprintBar(
            StartUtc: Epoch.Add(span * index),
            EndUtc: Epoch.Add(span * (index + 1)),
            Rows: rows,
            PocPrice: 100,
            VolumeCentroid: 100,
            BuyCentroid: 100.03571428571429,
            SellCentroid: 99.95,
            BuyVolume: 35,
            SellVolume: 25,
            Delta: 10,
            CumulativeDelta: 10L * (index + 1),
            StackedBuy: 0,
            StackedSell: 0,
            Quality: FeedQuality.RealTape);
    }

    private static FootprintFeatureRow[] Rows() =>
    [
        Row(100.25, buy: 10, sell: 5),
        Row(100.00, buy: 20, sell: 10),
        Row(99.75, buy: 5, sell: 10),
    ];

    private static FootprintFeatureRow Row(double price, long buy, long sell) =>
        new(price, buy, sell, BidImbalance: false, AskImbalance: false, ZeroBid: sell == 0, ZeroAsk: buy == 0);

    private static FootprintForecastQuantiles Q(double q10, double q50, double q90) => new(q10, q50, q90);

    private static FootprintForecastModelMetadata Model() => new("test-provider", "test-model", "1");

    private static FootprintHorizonForecast Forecast(
        FootprintForecastRequest request,
        int horizonBars,
        TimeSpan? targetOffset = null,
        FootprintForecastQuantiles? poc = null,
        FootprintForecastQuantiles? low = null,
        FootprintForecastQuantiles? high = null,
        FootprintForecastQuantiles? buyPoc = null,
        FootprintForecastQuantiles? sellPoc = null,
        FootprintForecastQuantiles? volume = null,
        FootprintForecastQuantiles? deltaFraction = null,
        bool includeDeltaMedian = false)
    {
        var start = request.CutoffUtc + request.Coordinate.Interval * (horizonBars - 1) + (targetOffset ?? TimeSpan.Zero);
        return new FootprintHorizonForecast(
            horizonBars,
            start,
            start + request.Coordinate.Interval,
            poc ?? Q(100, 100.5, 101),
            low ?? Q(99, 99.5, 100),
            high ?? Q(101, 101.5, 102),
            buyPoc ?? Q(100.25, 100.75, 101.25),
            sellPoc ?? Q(99.75, 100.25, 100.75),
            volume ?? Q(100, 120, 140),
            deltaFraction ?? Q(-0.2, 0.25, 0.6),
            includeDeltaMedian);
    }
}
