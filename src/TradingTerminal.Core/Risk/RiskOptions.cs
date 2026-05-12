namespace TradingTerminal.Core.Risk;

/// <summary>
/// Caps enforced by <see cref="IRiskManager"/>. Zero or negative values disable a cap
/// (the equivalent of "no limit"). Defaults are permissive — opt in by editing your
/// host configuration or by overriding via DI.
/// </summary>
public sealed class RiskOptions
{
    public const string SectionName = "Risk";

    /// <summary>Maximum absolute net position per symbol, in contracts/shares. 0 = disabled.</summary>
    public long MaxPositionPerSymbol { get; set; }

    /// <summary>Daily realised-loss cap in account currency (e.g. USD). 0 = disabled.</summary>
    public double MaxDailyLoss { get; set; }

    /// <summary>
    /// Treated as a flat multiplier from price to notional. Backtest sessions pull this
    /// from <c>BacktestConfig.ContractMultiplier</c>; live trading uses 1.0 unless wired
    /// in by broker code that knows the contract's tick value.
    /// </summary>
    public double DefaultContractMultiplier { get; set; } = 1.0;
}
