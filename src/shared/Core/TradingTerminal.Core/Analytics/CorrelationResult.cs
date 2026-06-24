namespace TradingTerminal.Core.Analytics;

/// <summary>
/// A computed correlation matrix over a set of instruments. <see cref="Labels"/> indexes both
/// dimensions of <see cref="Values"/> (row <c>i</c> / column <c>j</c> is the correlation between
/// <c>Labels[i]</c> and <c>Labels[j]</c>); the matrix is symmetric with a unit diagonal.
/// <see cref="SampleCount"/> is the number of paired return observations the correlations were
/// computed from (after timestamp alignment), so the UI can show how robust the figures are.
/// </summary>
public sealed record CorrelationMatrix(
    IReadOnlyList<string> Labels,
    double[,] Values,
    int SampleCount)
{
    public int Size => Labels.Count;

    public double At(int row, int col) => Values[row, col];
}
