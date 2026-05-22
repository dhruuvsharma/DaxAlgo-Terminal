namespace TradingTerminal.Core.Regime;

/// <summary>
/// One scored sub-signal of the composite. <see cref="Contribution"/> is the points this
/// category adds to the 0–100 composite (<c>Score × Weight</c>). <see cref="Degraded"/> is
/// true when the category fell back to neutral because its inputs were unavailable.
/// <see cref="Detail"/> is a short, human-readable summary of the driving inputs (e.g.
/// "VIX 18.4 · contango").
/// </summary>
public sealed record RegimeCategoryScore(
    RegimeCategory Category,
    int Score,
    double Weight,
    double Contribution,
    bool Degraded,
    string Detail);
