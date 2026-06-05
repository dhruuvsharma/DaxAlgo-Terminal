namespace TradingTerminal.Heatmap;

/// <summary>Marker for anything the shared <see cref="HeatmapRenderer"/> can draw — a gridded
/// <see cref="HeatmapFrame"/> or a scatter <see cref="BubbleFrame"/>.</summary>
public interface IHeatmapFrame
{
}

/// <summary>How a heatmap's cell values map to colour.</summary>
public enum HeatmapPalette
{
    /// <summary>Low → high single ramp (Turbo). For magnitudes — resting size, traded volume, volatility.</summary>
    Sequential,

    /// <summary>Diverging ramp centred on zero (Balance: negative → blue, 0 → white, positive → red).
    /// For signed quantities — order-book imbalance, correlation.</summary>
    Diverging,
}

/// <summary>
/// A gridded heatmap frame. <see cref="Cells"/> is indexed <c>[row, col]</c> with row 0 drawn at the
/// <em>top</em>. The four extents place the grid in axis coordinates (price or instrument-row on Y,
/// time-column on X). <see cref="Overlay"/> is an optional per-column y-value track (e.g. the mid
/// price; <see cref="double.NaN"/> = gap). <see cref="RowLabels"/> are optional left-axis tick labels
/// (e.g. instrument symbols); when present there must be one per row.
/// </summary>
public sealed record HeatmapFrame(
    double[,] Cells,
    double XMin, double XMax,
    double YMin, double YMax,
    HeatmapPalette Palette,
    double[]? Overlay = null,
    IReadOnlyList<string>? RowLabels = null) : IHeatmapFrame;

/// <summary>Which side initiated a trade — drives the bubble colour.</summary>
public enum BubbleSide
{
    Unknown = 0,
    Buy,
    Sell,
}

/// <summary>One trade bubble: positioned at (<see cref="X"/> = time as OADate, <see cref="Price"/>),
/// drawn at <see cref="SizePx"/> pixels and coloured by <see cref="Side"/>.</summary>
public readonly record struct HeatBubble(double X, double Price, float SizePx, BubbleSide Side);

/// <summary>
/// A scatter "bubble" frame — trade prints as circles sized by volume and coloured by aggressor side,
/// positioned at (time, price). The X extent is in OADate so the renderer can use a date axis.
/// </summary>
public sealed record BubbleFrame(
    IReadOnlyList<HeatBubble> Bubbles,
    double XMin, double XMax,
    double YMin, double YMax) : IHeatmapFrame;
