namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>What an authoring session is producing.</summary>
public enum AuthoringKind
{
    /// <summary>A trading strategy: a kernel that emits signals and trades its virtual book.</summary>
    Strategy = 0,

    /// <summary>A visualizer: renders market data and computed state, and never trades.</summary>
    Visualizer = 1,
}
