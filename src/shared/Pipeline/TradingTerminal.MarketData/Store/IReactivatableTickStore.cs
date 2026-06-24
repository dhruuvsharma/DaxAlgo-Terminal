namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Implemented by a tick store that can be brought from inert to live <i>after</i> construction —
/// specifically the QuestDB store, which captures availability once at startup. When QuestDB is
/// started later (File → Start QuestDB), <see cref="TryActivate"/> creates the schema + sender and
/// flips persistence on so ticks start landing without an app restart.
/// </summary>
internal interface IReactivatableTickStore
{
    /// <summary>True once the store is live and persisting.</summary>
    bool IsActive { get; }

    /// <summary>Attempt to go live now. Idempotent — returns true if already active or activation
    /// succeeds; false if the backend still can't be reached.</summary>
    bool TryActivate();
}
