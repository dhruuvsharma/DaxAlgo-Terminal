using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.MarketData.AdvancedRegime;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>The five layers of the feed-forward regime net, left → right.</summary>
public enum GraphNodeKind
{
    Company,    // input layer — one per constituent
    Timeframe,  // hidden layer 1 — the eight regime timeframes
    Indicator,  // hidden layer 2 — the regime indicators
    Output,     // the per-stock / aggregate output activation
    Signal,     // the final composite (output × weight summed)
}

/// <summary>Band → hex colour (bipolar green→red). Plain strings so the view can bind through the
/// shared <c>StringToBrushConverter</c> and the models stay free of WPF brush types.</summary>
internal static class BandColors
{
    public static string Hex(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "#16C784",
        CellSignal.Up => "#3FB37A",
        CellSignal.Down => "#E0555A",
        CellSignal.StrongDown => "#E6383C",
        _ => "#6B7280",
    };
}

/// <summary>
/// One node (neuron) in the feed-forward net. Position is mutable so the view's drag gesture can
/// move it; <see cref="CenterX"/>/<see cref="CenterY"/> are derived so edges bound to them follow.
/// <see cref="Score"/>/<see cref="Band"/> drive colour; <see cref="Size"/> the diameter.
/// </summary>
public sealed partial class GraphNode : ObservableObject
{
    public required string Id { get; init; }
    public required GraphNodeKind Kind { get; init; }

    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _subLabel = string.Empty;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private double _size = 40;

    [ObservableProperty] private double _score;
    [ObservableProperty] private CellSignal _band = CellSignal.Neutral;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasData = true;

    public double CenterX => X + Size / 2;
    public double CenterY => Y + Size / 2;

    /// <summary>Fill colour hex, derived from <see cref="Band"/> (bound via StringToBrushConverter).</summary>
    public string ColorHex => BandColors.Hex(Band);

    /// <summary>Label font size by layer.</summary>
    public double LabelFontSize => Kind switch
    {
        GraphNodeKind.Signal => 15,
        GraphNodeKind.Output => 12,
        GraphNodeKind.Timeframe => 11,
        GraphNodeKind.Company => 9,
        _ => 8,
    };

    partial void OnXChanged(double value) { OnPropertyChanged(nameof(CenterX)); }
    partial void OnYChanged(double value) { OnPropertyChanged(nameof(CenterY)); }
    partial void OnSizeChanged(double value) { OnPropertyChanged(nameof(CenterX)); OnPropertyChanged(nameof(CenterY)); }
    partial void OnBandChanged(CellSignal value) { OnPropertyChanged(nameof(ColorHex)); }
}

/// <summary>
/// A synapse between two nodes. The view binds a line's endpoints to <c>From.CenterX/Y</c> and
/// <c>To.CenterX/Y</c> so edges follow node drags. <see cref="Thickness"/> / <see cref="Band"/> /
/// <see cref="Opacity"/> are observable so a refresh (and the focus highlight) restyle in place.
/// </summary>
public sealed partial class GraphEdge : ObservableObject
{
    public required GraphNode From { get; init; }
    public required GraphNode To { get; init; }

    [ObservableProperty] private double _thickness = 1.0;
    [ObservableProperty] private CellSignal _band = CellSignal.Neutral;
    [ObservableProperty] private double _opacity = 0.16;

    /// <summary>Stroke colour hex, derived from <see cref="Band"/>.</summary>
    public string ColorHex => BandColors.Hex(Band);

    partial void OnBandChanged(CellSignal value) { OnPropertyChanged(nameof(ColorHex)); }
}
