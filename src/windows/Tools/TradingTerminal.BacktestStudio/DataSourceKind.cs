using TradingTerminal.Core.Domain;

namespace TradingTerminal.BacktestStudio;

/// <summary>Where the Studio pulls market data from for a run.</summary>
public enum DataSourceKind
{
    /// <summary>Built-in seeded random walk — zero setup.</summary>
    Synthetic,

    /// <summary>A recorded parquet tick file.</summary>
    Parquet,

    /// <summary>The canonical local store, scoped to a broker + symbol + date range.</summary>
    Store,
}

/// <summary>User-facing label for one supported Studio bar interval.</summary>
public sealed record StudioTimeframeOption(BarSize Value, string Label)
{
    public static IReadOnlyList<StudioTimeframeOption> All { get; } =
        Enum.GetValues<BarSize>()
            .Select(value => new StudioTimeframeOption(value, value.ToDisplayString()))
            .ToArray();
}
