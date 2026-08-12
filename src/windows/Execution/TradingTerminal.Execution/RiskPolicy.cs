using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Execution;

/// <summary>Fault-as-value outcomes from constructing a pre-trade risk policy.</summary>
public enum RiskPolicyFault : byte
{
    /// <summary>The policy was created.</summary>
    None = 0,

    /// <summary>The policy id or version is missing or outside the supported length.</summary>
    InvalidIdentity = 1,

    /// <summary>One or more caps are non-positive, fractional where whole units are required, or invalid.</summary>
    InvalidLimits = 2,
}

/// <summary>
/// Exact buyer-owned caps for the ADR D6 private execution boundary. Order caps apply to the
/// target-to-current delta; position and gross caps apply to the projected post-intent exposure.
/// </summary>
/// <param name="MaximumOrderQuantity">Maximum absolute quantity admitted for one order.</param>
/// <param name="MaximumOrderNotional">Maximum exact notional admitted for one order.</param>
/// <param name="MaximumAbsolutePositionPerInstrument">Maximum projected absolute instrument position.</param>
/// <param name="MaximumGrossExposure">Maximum projected account gross exposure.</param>
/// <param name="DailyLossLimit">Maximum combined realized and mark-to-market loss for one UTC risk day.</param>
public readonly record struct RiskLimits(
    ScaledQuantity MaximumOrderQuantity,
    ScaledMoney MaximumOrderNotional,
    ScaledQuantity MaximumAbsolutePositionPerInstrument,
    ScaledMoney MaximumGrossExposure,
    ScaledMoney DailyLossLimit);

/// <summary>
/// Immutable, versioned pre-trade policy for the unified-execution ADR D6 private boundary and D7
/// backtest-first path. Its SHA-256 binds the exact id, version, coefficients, scales, and cap order.
/// </summary>
public sealed class RiskPolicy
{
    private RiskPolicy(string policyId, string policyVersion, string policyHash, RiskLimits limits)
    {
        PolicyId = policyId;
        PolicyVersion = policyVersion;
        PolicyHash = policyHash;
        Limits = limits;
    }

    /// <summary>Gets the stable host-assigned policy id.</summary>
    public string PolicyId { get; }

    /// <summary>Gets the immutable host-assigned policy version.</summary>
    public string PolicyVersion { get; }

    /// <summary>Gets the lowercase SHA-256 of the canonical exact policy content.</summary>
    public string PolicyHash { get; }

    /// <summary>Gets the exact caps bound by <see cref="PolicyHash"/>.</summary>
    public RiskLimits Limits { get; }

    /// <summary>
    /// Validates and creates a policy without clamping or defaulting an invalid limit. Construction
    /// performs no I/O and the same exact input always produces the same hash.
    /// </summary>
    public static RiskPolicyFault TryCreate(
        string? policyId,
        string? policyVersion,
        in RiskLimits limits,
        out RiskPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(policyId) || policyId.Length > 128 ||
            string.IsNullOrWhiteSpace(policyVersion) || policyVersion.Length > 128)
        {
            return RiskPolicyFault.InvalidIdentity;
        }

        if (!TryPositiveWholeUnits(limits.MaximumOrderQuantity, out _) ||
            !IsPositive(limits.MaximumOrderNotional) ||
            !TryPositiveWholeUnits(limits.MaximumAbsolutePositionPerInstrument, out _) ||
            !IsPositive(limits.MaximumGrossExposure) ||
            !IsPositive(limits.DailyLossLimit))
        {
            return RiskPolicyFault.InvalidLimits;
        }

        var hash = ComputeHash(policyId, policyVersion, limits);
        policy = new RiskPolicy(policyId, policyVersion, hash, limits);
        return RiskPolicyFault.None;
    }

    private static bool TryPositiveWholeUnits(ScaledQuantity quantity, out long units) =>
        quantity.TryGetWholeUnits(out units) && units > 0;

    private static bool IsPositive(ScaledMoney money) =>
        money.IsValid && money.Coefficient > 0;

    private static string ComputeHash(string policyId, string policyVersion, in RiskLimits limits)
    {
        var canonical = string.Join(
            '|',
            "risk-policy-v1",
            LengthPrefix(policyId),
            LengthPrefix(policyVersion),
            Exact(limits.MaximumOrderQuantity.Coefficient, limits.MaximumOrderQuantity.Scale),
            Exact(limits.MaximumOrderNotional.Coefficient, limits.MaximumOrderNotional.Scale),
            Exact(
                limits.MaximumAbsolutePositionPerInstrument.Coefficient,
                limits.MaximumAbsolutePositionPerInstrument.Scale),
            Exact(limits.MaximumGrossExposure.Coefficient, limits.MaximumGrossExposure.Scale),
            Exact(limits.DailyLossLimit.Coefficient, limits.DailyLossLimit.Scale));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string LengthPrefix(string value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value}");

    private static string Exact(long coefficient, byte scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{coefficient}@{scale}");
}
