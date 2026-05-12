using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Microprice;

public sealed class MicropriceStrategy : ITradingStrategy
{
    public string Id => "microprice.deviation";
    public string DisplayName => "Microprice deviation (microstructure)";
    public string Description => "Pure microstructure scalper. Trades the deviation between the size-weighted microprice and the simple mid.";
}