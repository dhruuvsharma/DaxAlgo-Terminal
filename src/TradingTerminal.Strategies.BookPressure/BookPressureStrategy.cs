using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.BookPressure;

public sealed class BookPressureStrategy : ITradingStrategy
{
    public string Id => "book.pressure";
    public string DisplayName => "Order-book pressure / cumulative imbalance (L2)";
    public string Description => "Multi-level queue imbalance signal (L1 approximation when no DepthSnapshot is available).";
}