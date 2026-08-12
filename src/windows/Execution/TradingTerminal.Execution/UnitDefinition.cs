namespace TradingTerminal.Execution;

/// <summary>The fixed buyer-selectable unit choices required by unified-execution ADR D4.</summary>
public enum UnitDefinitionKind : byte
{
    /// <summary>A fixed whole-contract target at full signal strength.</summary>
    FixedContracts = 0,

    /// <summary>Whole contracts sized from a basis-point fraction of current equity at risk.</summary>
    PercentOfEquityAtRisk = 1,

    /// <summary>Whole contracts sized from an exact fixed cash-risk budget.</summary>
    FixedCashRisk = 2,

    /// <summary>Whole contracts sized inversely to an exact volatility observation.</summary>
    VolatilityScaled = 3,
}

/// <summary>
/// One buyer-owned unit definition from ADR D4. Factories retain untrusted values without silently
/// normalizing them; <see cref="SignalExecutionPolicy.Evaluate"/> returns validation faults as values.
/// </summary>
public readonly record struct UnitDefinition
{
    private UnitDefinition(
        UnitDefinitionKind kind,
        long fixedContracts,
        int equityRiskBasisPoints,
        ScaledMoney cashRisk,
        ScaledPrice sizingRiskDistance,
        ScaledPrice volatility,
        int volatilityMultipleBasisPoints)
    {
        Kind = kind;
        FixedContractCount = fixedContracts;
        EquityRiskBasisPoints = equityRiskBasisPoints;
        CashRisk = cashRisk;
        SizingRiskDistance = sizingRiskDistance;
        Volatility = volatility;
        VolatilityMultipleBasisPoints = volatilityMultipleBasisPoints;
    }

    /// <summary>Gets the selected definition kind.</summary>
    public UnitDefinitionKind Kind { get; }

    /// <summary>Gets the full-strength contract count for <see cref="UnitDefinitionKind.FixedContracts"/>.</summary>
    public long FixedContractCount { get; }

    /// <summary>Gets the equity-risk budget in basis points.</summary>
    public int EquityRiskBasisPoints { get; }

    /// <summary>Gets the fixed exact cash budget for cash-risk or volatility sizing.</summary>
    public ScaledMoney CashRisk { get; }

    /// <summary>Gets the exact per-contract price risk used by fixed-cash and equity-risk sizing.</summary>
    public ScaledPrice SizingRiskDistance { get; }

    /// <summary>Gets the exact volatility observation used by volatility sizing.</summary>
    public ScaledPrice Volatility { get; }

    /// <summary>Gets the volatility multiplier in basis points.</summary>
    public int VolatilityMultipleBasisPoints { get; }

    /// <summary>The conservative buyer default: one fixed contract, still bounded by buyer caps.</summary>
    public static UnitDefinition ConservativeDefault => FixedContracts(1);

    /// <summary>Creates a fixed-contract definition.</summary>
    public static UnitDefinition FixedContracts(long contracts) =>
        new(UnitDefinitionKind.FixedContracts, contracts, 0, default, default, default, 0);

    /// <summary>Creates a percent-of-equity-at-risk definition.</summary>
    public static UnitDefinition PercentOfEquityAtRisk(int basisPoints, ScaledPrice riskDistance) =>
        new(UnitDefinitionKind.PercentOfEquityAtRisk, 0, basisPoints, default, riskDistance, default, 0);

    /// <summary>Creates a fixed-cash-risk definition.</summary>
    public static UnitDefinition FixedCashRisk(ScaledMoney cashRisk, ScaledPrice riskDistance) =>
        new(UnitDefinitionKind.FixedCashRisk, 0, 0, cashRisk, riskDistance, default, 0);

    /// <summary>Creates an inverse-volatility definition with an exact cash budget.</summary>
    public static UnitDefinition VolatilityScaled(
        ScaledMoney cashRisk,
        ScaledPrice volatility,
        int volatilityMultipleBasisPoints = 10_000) =>
        new(UnitDefinitionKind.VolatilityScaled, 0, 0, cashRisk, default, volatility, volatilityMultipleBasisPoints);
}

/// <summary>Exact host-provided market/account inputs for one policy evaluation.</summary>
public readonly record struct SignalExecutionInputs(
    TradingTerminal.Core.Domain.InstrumentId Instrument,
    ScaledPrice ReferencePrice,
    ScaledMoney Equity,
    ScaledQuantity CurrentPosition,
    ScaledRatio ContractMultiplier);

/// <summary>Buyer caps that always take precedence over a candidate target under ADR D4.</summary>
public readonly record struct BuyerExecutionCaps(
    ScaledQuantity MaximumAbsoluteUnits,
    ScaledMoney? MaximumNotional = null,
    ScaledMoney? MaximumCashRisk = null)
{
    /// <summary>A one-contract absolute ceiling; optional money caps remain buyer-configurable.</summary>
    public static BuyerExecutionCaps ConservativeDefault => new(ScaledQuantity.FromWhole(1));
}

/// <summary>Exact cost assumptions included in risk sizing and recorded on every accepted intent.</summary>
public readonly record struct SignalCostAssumptions(
    ScaledMoney EntrySlippagePerUnit,
    ScaledMoney ExitSlippagePerUnit,
    ScaledMoney FeesPerRoundTripUnit)
{
    /// <summary>Zero-cost assumptions.</summary>
    public static SignalCostAssumptions Zero => new(ScaledMoney.Zero, ScaledMoney.Zero, ScaledMoney.Zero);
}

/// <summary>Immutable host-owned policy configuration.</summary>
public readonly record struct SignalExecutionPolicyOptions(
    SignalCostAssumptions Costs,
    BuyerExecutionCaps Caps,
    bool AttachSizingRiskAsProtectiveStop = false,
    int ProfitTargetMultipleBasisPoints = 0)
{
    /// <summary>One-contract conservative defaults with no assumed costs or protective prices.</summary>
    public static SignalExecutionPolicyOptions ConservativeDefault =>
        new(SignalCostAssumptions.Zero, BuyerExecutionCaps.ConservativeDefault);
}
