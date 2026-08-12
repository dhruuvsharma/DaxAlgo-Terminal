using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Ml;

/// <summary>Identifies one footprint series without coupling callers to a model implementation.</summary>
public sealed record FootprintForecastCoordinate
{
    public FootprintForecastCoordinate(
        string instrumentKey,
        BrokerKind source,
        TimeSpan interval,
        double rowSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentKey);
        if (!Enum.IsDefined(typeof(BrokerKind), source))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        if (!double.IsFinite(rowSize) || rowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowSize), "Row size must be finite and positive.");

        InstrumentKey = instrumentKey.Trim();
        Source = source;
        Interval = interval;
        RowSize = rowSize;
    }

    public string InstrumentKey { get; }
    public BrokerKind Source { get; }
    public TimeSpan Interval { get; }
    public double RowSize { get; }
}

/// <summary>
/// A bounded inference request containing complete footprint bars. Bars and their row collections
/// are defensively copied so the history cannot change after construction.
/// </summary>
public sealed class FootprintForecastRequest
{
    public const int MaximumHistoryBars = 4_096;
    public const int MaximumHorizonBars = 256;

    public FootprintForecastRequest(
        FootprintForecastCoordinate coordinate,
        IEnumerable<FootprintBar> history,
        DateTime cutoffUtc,
        int horizonBars)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        ArgumentNullException.ThrowIfNull(history);

        if (cutoffUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cutoff must be UTC.", nameof(cutoffUtc));
        if (horizonBars is < 1 or > MaximumHorizonBars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(horizonBars),
                $"Horizon must be between 1 and {MaximumHorizonBars} bars.");
        }

        var sourceBars = history.Take(MaximumHistoryBars + 1).ToArray();
        if (sourceBars.Length == 0)
            throw new ArgumentException("At least one completed footprint bar is required.", nameof(history));
        if (sourceBars.Length > MaximumHistoryBars)
        {
            throw new ArgumentException(
                $"History cannot exceed {MaximumHistoryBars} bars.",
                nameof(history));
        }

        var frozenBars = new FootprintBar[sourceBars.Length];
        DateTime? previousEndUtc = null;
        for (var index = 0; index < sourceBars.Length; index++)
        {
            var bar = sourceBars[index]
                      ?? throw new ArgumentException($"History bar {index} is null.", nameof(history));
            frozenBars[index] = ValidateAndFreezeBar(bar, index, coordinate, previousEndUtc, nameof(history));
            previousEndUtc = bar.EndUtc;
        }

        if (cutoffUtc != frozenBars[^1].EndUtc)
            throw new ArgumentException("Cutoff must equal the final history bar's end time.", nameof(cutoffUtc));

        try
        {
            var forecastTicks = checked(coordinate.Interval.Ticks * (long)horizonBars);
            _ = cutoffUtc.AddTicks(forecastTicks);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(horizonBars),
                "The requested horizon exceeds the UTC timestamp range.");
        }

        Coordinate = coordinate;
        History = Array.AsReadOnly(frozenBars);
        CutoffUtc = cutoffUtc;
        HorizonBars = horizonBars;
    }

    public FootprintForecastCoordinate Coordinate { get; }
    public IReadOnlyList<FootprintBar> History { get; }
    public DateTime CutoffUtc { get; }
    public int HorizonBars { get; }

    private static FootprintBar ValidateAndFreezeBar(
        FootprintBar bar,
        int index,
        FootprintForecastCoordinate coordinate,
        DateTime? previousEndUtc,
        string parameterName)
    {
        if (bar.StartUtc.Kind != DateTimeKind.Utc || bar.EndUtc.Kind != DateTimeKind.Utc)
            throw InvalidBar(index, "timestamps must be UTC", parameterName);
        if (bar.EndUtc <= bar.StartUtc)
            throw InvalidBar(index, "end time must follow start time", parameterName);
        if (bar.EndUtc - bar.StartUtc != coordinate.Interval)
            throw InvalidBar(index, "duration must exactly match the coordinate interval", parameterName);
        if (previousEndUtc is not null && bar.StartUtc != previousEndUtc.Value)
            throw InvalidBar(index, "must be contiguous with the prior bar", parameterName);

        if (bar.Rows is null || bar.Rows.Count == 0)
            throw InvalidBar(index, "must contain complete price rows", parameterName);
        if (!double.IsFinite(bar.PocPrice) ||
            !double.IsFinite(bar.VolumeCentroid) ||
            !double.IsFinite(bar.BuyCentroid) ||
            !double.IsFinite(bar.SellCentroid))
        {
            throw InvalidBar(index, "aggregated prices must be finite", parameterName);
        }
        if (bar.BuyVolume < 0 || bar.SellVolume < 0)
            throw InvalidBar(index, "side volumes cannot be negative", parameterName);
        if (bar.Delta != bar.BuyVolume - bar.SellVolume)
            throw InvalidBar(index, "delta must equal buy volume minus sell volume", parameterName);
        if (bar.StackedBuy < 0 || bar.StackedSell < 0 ||
            bar.StackedBuy > bar.Rows.Count || bar.StackedSell > bar.Rows.Count)
        {
            throw InvalidBar(index, "stacked-run counts are outside the row range", parameterName);
        }
        if (!Enum.IsDefined(typeof(FeedQuality), bar.Quality))
            throw InvalidBar(index, "feed quality is unknown", parameterName);

        var rows = bar.Rows.ToArray();
        long rowBuy = 0;
        long rowSell = 0;
        long largestRowVolume = -1;
        var pocMatchesLargestRow = false;

        try
        {
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = rows[rowIndex]
                          ?? throw InvalidBar(index, $"row {rowIndex} is null", parameterName);
                if (!double.IsFinite(row.Price))
                    throw InvalidBar(index, $"row {rowIndex} price must be finite", parameterName);
                if (row.BuyVolume < 0 || row.SellVolume < 0)
                    throw InvalidBar(index, $"row {rowIndex} volumes cannot be negative", parameterName);
                if (row.ZeroBid != (row.SellVolume == 0) || row.ZeroAsk != (row.BuyVolume == 0))
                    throw InvalidBar(index, $"row {rowIndex} zero-volume flags are inconsistent", parameterName);

                if (rowIndex > 0)
                {
                    var priceDifference = rows[rowIndex - 1].Price - row.Price;
                    if (priceDifference <= 0 || !IsGridMultiple(priceDifference, coordinate.RowSize))
                    {
                        throw InvalidBar(
                            index,
                            "rows must be strictly high-to-low on the coordinate row grid",
                            parameterName);
                    }
                }

                rowBuy = checked(rowBuy + row.BuyVolume);
                rowSell = checked(rowSell + row.SellVolume);
                var rowVolume = checked(row.BuyVolume + row.SellVolume);
                if (rowVolume > largestRowVolume)
                {
                    largestRowVolume = rowVolume;
                    pocMatchesLargestRow = NearlyEqual(row.Price, bar.PocPrice, coordinate.RowSize);
                }
                else if (rowVolume == largestRowVolume && NearlyEqual(row.Price, bar.PocPrice, coordinate.RowSize))
                {
                    pocMatchesLargestRow = true;
                }
            }
        }
        catch (OverflowException)
        {
            throw InvalidBar(index, "row volume totals overflow their supported range", parameterName);
        }

        if (rowBuy != bar.BuyVolume || rowSell != bar.SellVolume)
            throw InvalidBar(index, "row volumes must equal the bar side totals", parameterName);
        if (!pocMatchesLargestRow)
            throw InvalidBar(index, "POC must identify a maximum-volume row", parameterName);

        var high = rows[0].Price;
        var low = rows[^1].Price;
        if (!IsWithin(bar.VolumeCentroid, low, high) ||
            (bar.BuyVolume > 0 && !IsWithin(bar.BuyCentroid, low, high)) ||
            (bar.SellVolume > 0 && !IsWithin(bar.SellCentroid, low, high)))
        {
            throw InvalidBar(index, "centroids must lie inside the bar's price range", parameterName);
        }

        return bar with { Rows = Array.AsReadOnly(rows) };
    }

    private static bool IsGridMultiple(double difference, double rowSize)
    {
        var steps = difference / rowSize;
        if (!double.IsFinite(steps)) return false;
        var nearest = Math.Round(steps);
        return nearest >= 1 && Math.Abs(steps - nearest) <= 1e-9 * Math.Max(1, Math.Abs(steps));
    }

    private static bool NearlyEqual(double left, double right, double scale) =>
        Math.Abs(left - right) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(scale)));

    private static bool IsWithin(double value, double low, double high) => value >= low && value <= high;

    private static ArgumentException InvalidBar(int index, string reason, string parameterName) =>
        new($"History bar {index} {reason}.", parameterName);
}

/// <summary>A finite, ordered marginal q10/q50/q90 distribution for one forecast target.</summary>
public sealed record FootprintForecastQuantiles
{
    public FootprintForecastQuantiles(double q10, double q50, double q90)
    {
        if (!double.IsFinite(q10) || !double.IsFinite(q50) || !double.IsFinite(q90))
            throw new ArgumentOutOfRangeException(nameof(q10), "Forecast quantiles must be finite.");
        if (q10 > q50 || q50 > q90)
            throw new ArgumentException("Forecast quantiles must satisfy q10 <= q50 <= q90.");

        Q10 = q10;
        Q50 = q50;
        Q90 = q90;
    }

    public double Q10 { get; }
    public double Q50 { get; }
    public double Q90 { get; }
}

/// <summary>One target interval and its marginal footprint forecast distributions.</summary>
public sealed class FootprintHorizonForecast
{
    public FootprintHorizonForecast(
        int horizonBars,
        DateTime targetStartUtc,
        DateTime targetEndUtc,
        FootprintForecastQuantiles poc,
        FootprintForecastQuantiles low,
        FootprintForecastQuantiles high,
        FootprintForecastQuantiles buyPoc,
        FootprintForecastQuantiles sellPoc,
        FootprintForecastQuantiles volume,
        FootprintForecastQuantiles deltaFraction,
        bool includeDeltaMedian = false)
    {
        if (horizonBars <= 0)
            throw new ArgumentOutOfRangeException(nameof(horizonBars));
        if (targetStartUtc.Kind != DateTimeKind.Utc || targetEndUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Target interval timestamps must be UTC.");
        if (targetEndUtc <= targetStartUtc)
            throw new ArgumentException("Target interval end must follow its start.");

        ArgumentNullException.ThrowIfNull(poc);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(high);
        ArgumentNullException.ThrowIfNull(buyPoc);
        ArgumentNullException.ThrowIfNull(sellPoc);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(deltaFraction);

        EnsureAtOrBelow(low, high, "Low cannot exceed high.");
        EnsureWithin(poc, low, high, "POC");
        EnsureWithin(buyPoc, low, high, "Buy POC");
        EnsureWithin(sellPoc, low, high, "Sell POC");
        if (volume.Q10 < 0)
            throw new ArgumentException("Volume quantiles cannot be negative.", nameof(volume));
        if (deltaFraction.Q10 < -1 || deltaFraction.Q90 > 1)
        {
            throw new ArgumentException(
                "Delta-fraction quantiles must remain inside [-1, 1].",
                nameof(deltaFraction));
        }

        double? deltaMedian = null;
        if (includeDeltaMedian)
        {
            deltaMedian = volume.Q50 * deltaFraction.Q50;
            if (!double.IsFinite(deltaMedian.Value))
                throw new ArgumentOutOfRangeException(nameof(volume), "The delta point estimate must be finite.");
        }

        HorizonBars = horizonBars;
        TargetStartUtc = targetStartUtc;
        TargetEndUtc = targetEndUtc;
        Poc = poc;
        Low = low;
        High = high;
        BuyPoc = buyPoc;
        SellPoc = sellPoc;
        Volume = volume;
        DeltaFraction = deltaFraction;
        DeltaMedian = deltaMedian;
    }

    public int HorizonBars { get; }
    public DateTime TargetStartUtc { get; }
    public DateTime TargetEndUtc { get; }
    public FootprintForecastQuantiles Poc { get; }
    public FootprintForecastQuantiles Low { get; }
    public FootprintForecastQuantiles High { get; }
    public FootprintForecastQuantiles BuyPoc { get; }
    public FootprintForecastQuantiles SellPoc { get; }
    public FootprintForecastQuantiles Volume { get; }
    public FootprintForecastQuantiles DeltaFraction { get; }

    /// <summary>
    /// Optional point estimate computed as Q50(volume) multiplied by Q50(delta fraction).
    /// It is not a quantile of the product distribution.
    /// </summary>
    public double? DeltaMedian { get; }

    private static void EnsureWithin(
        FootprintForecastQuantiles value,
        FootprintForecastQuantiles low,
        FootprintForecastQuantiles high,
        string name)
    {
        if (value.Q10 < low.Q10 || value.Q50 < low.Q50 || value.Q90 < low.Q90 ||
            value.Q10 > high.Q10 || value.Q50 > high.Q50 || value.Q90 > high.Q90)
        {
            throw new ArgumentException($"{name} quantiles must lie between low and high.");
        }
    }

    private static void EnsureAtOrBelow(
        FootprintForecastQuantiles lower,
        FootprintForecastQuantiles upper,
        string message)
    {
        if (lower.Q10 > upper.Q10 || lower.Q50 > upper.Q50 || lower.Q90 > upper.Q90)
            throw new ArgumentException(message);
    }
}

/// <summary>Runtime-neutral identity for the model that produced an available forecast.</summary>
public sealed record FootprintForecastModelMetadata
{
    public FootprintForecastModelMetadata(string provider, string model, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Provider = provider;
        Model = model;
        Version = version;
    }

    public string Provider { get; }
    public string Model { get; }
    public string Version { get; }
}

public enum FootprintForecastStatus
{
    Unavailable = 0,
    Available = 1,
    InsufficientHistory = 2,
    Failed = 3,
}

/// <summary>An available N-horizon batch, or an unavailable result with no forecasts.</summary>
public sealed class FootprintForecastResult
{
    private FootprintForecastResult(
        FootprintForecastRequest request,
        FootprintForecastStatus status,
        FootprintForecastModelMetadata? model,
        IReadOnlyList<FootprintHorizonForecast> forecasts,
        string? message)
    {
        Coordinate = request.Coordinate;
        CutoffUtc = request.CutoffUtc;
        HorizonBars = request.HorizonBars;
        Status = status;
        Model = model;
        Forecasts = forecasts;
        Message = message;
    }

    public FootprintForecastCoordinate Coordinate { get; }
    public DateTime CutoffUtc { get; }
    public int HorizonBars { get; }
    public FootprintForecastStatus Status { get; }
    public FootprintForecastModelMetadata? Model { get; }
    public IReadOnlyList<FootprintHorizonForecast> Forecasts { get; }
    public string? Message { get; }

    public static FootprintForecastResult CreateAvailable(
        FootprintForecastRequest request,
        FootprintForecastModelMetadata model,
        IEnumerable<FootprintHorizonForecast> forecasts)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(forecasts);

        var batch = forecasts.Take(request.HorizonBars + 1).ToArray();
        if (batch.Length != request.HorizonBars)
        {
            throw new ArgumentException(
                $"An available result must contain exactly {request.HorizonBars} forecasts.",
                nameof(forecasts));
        }

        var expectedStartUtc = request.CutoffUtc;
        for (var index = 0; index < batch.Length; index++)
        {
            var forecast = batch[index]
                           ?? throw new ArgumentException($"Forecast {index} is null.", nameof(forecasts));
            var expectedEndUtc = expectedStartUtc.Add(request.Coordinate.Interval);
            if (forecast.HorizonBars != index + 1)
                throw new ArgumentException("Forecast horizons must be sequential from 1 through N.", nameof(forecasts));
            if (forecast.TargetStartUtc != expectedStartUtc || forecast.TargetEndUtc != expectedEndUtc)
            {
                throw new ArgumentException(
                    "Forecast target intervals must be exact, contiguous coordinate intervals after the cutoff.",
                    nameof(forecasts));
            }
            expectedStartUtc = expectedEndUtc;
        }

        return new FootprintForecastResult(
            request,
            FootprintForecastStatus.Available,
            model,
            Array.AsReadOnly(batch),
            message: null);
    }

    public static FootprintForecastResult CreateUnavailable(
        FootprintForecastRequest request,
        FootprintForecastStatus status = FootprintForecastStatus.Unavailable,
        FootprintForecastModelMetadata? model = null,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(typeof(FootprintForecastStatus), status) || status == FootprintForecastStatus.Available)
            throw new ArgumentOutOfRangeException(nameof(status), "An unavailable result cannot use Available status.");

        return new FootprintForecastResult(
            request,
            status,
            model,
            Array.AsReadOnly(Array.Empty<FootprintHorizonForecast>()),
            message);
    }
}

/// <summary>Model-agnostic asynchronous seam for footprint distribution forecasts.</summary>
public interface IFootprintForecastProvider
{
    Task<FootprintForecastResult> ForecastAsync(
        FootprintForecastRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>No-op provider used when no footprint forecasting capability is configured.</summary>
public sealed class NullFootprintForecastProvider : IFootprintForecastProvider
{
    public Task<FootprintForecastResult> ForecastAsync(
        FootprintForecastRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(FootprintForecastResult.CreateUnavailable(
            request,
            message: "No footprint forecast provider is configured."));
    }
}
