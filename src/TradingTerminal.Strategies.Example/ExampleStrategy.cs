using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Example;

public sealed class ExampleStrategy : ITradingStrategy
{
    public string Id => "example.nvda.3m";
    public string DisplayName => "Example Strategy";
    public string Description => "Streams NVDA on a 3-minute timeframe and renders a live candlestick chart.";
}
