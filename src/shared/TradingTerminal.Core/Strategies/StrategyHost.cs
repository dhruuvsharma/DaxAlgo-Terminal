namespace TradingTerminal.Core.Strategies;

/// <summary>
/// A concrete (view, view-model) pair plus metadata. The view and view-model are
/// declared as <see cref="object"/> here so Core has zero dependency on WPF —
/// callers cast to the platform types they need (UserControl + ViewModelBase in WPF).
/// </summary>
public sealed record StrategyHost(
    string StrategyId,
    string DisplayName,
    object View,
    object ViewModel);
