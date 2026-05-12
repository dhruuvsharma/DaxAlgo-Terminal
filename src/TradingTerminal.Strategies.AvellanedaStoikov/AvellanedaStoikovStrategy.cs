using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.AvellanedaStoikov;

public sealed class AvellanedaStoikovStrategy : ITradingStrategy
{
    public string Id => "avellaneda.stoikov";
    public string DisplayName => "Avellaneda-Stoikov market maker";
    public string Description => "Inventory-shifted reservation price; symmetric limit quotes with EWMA-variance widening. Cancel + repost every N ticks.";
}