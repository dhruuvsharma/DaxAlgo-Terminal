namespace TradingTerminal.Core.AiAnalyst;

public enum AiAnalystDecision
{
    /// <summary>No actionable verdict — the analyst either declined to call or was unavailable.</summary>
    NoCall,
    Long,
    Short,
}
