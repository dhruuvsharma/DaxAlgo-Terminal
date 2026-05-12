using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ThinBookFilter;

public sealed class ThinBookFilterStrategy : ITradingStrategy
{
    public string Id => "thin.book.filter";
    public string DisplayName => "Thin-book breakout filter (L2)";
    public string Description => "Breakout entry gated by depth threshold â€” skips entries during liquidity droughts.";
}