using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Which product edition an app shell ships as. Selected at composition time by the shell
/// executable (there is one <c>WinExe</c> per edition), not from configuration — a lower-tier
/// build simply never references or registers the higher-tier feature projects.
/// </summary>
/// <remarks>
/// Ordered least-to-most capable so <c>&gt;=</c> comparisons read naturally
/// (e.g. "credentialed brokers when <c>edition == Pro</c>").
/// </remarks>
public enum AppEdition
{
    /// <summary>Full broker selection with the base strategy, data, and settings surfaces.</summary>
    Basic,

    /// <summary>The private edition's higher-tier broker and feature composition.</summary>
    /// <remarks>
    /// The member is <c>Pro</c> because that is the product name, but the JSON name is pinned to
    /// <c>"Professional"</c>: this enum is serialized by name into the **signed** entitlement lease
    /// (<see cref="TradingTerminal.Core.Accounts.EntitlementLeaseWireDto"/>, and <c>PlanTier</c> on
    /// the platform side). Changing the wire value would invalidate every lease already issued and
    /// requires the desktop and the platform to ship in lockstep — a deployment decision, not a
    /// rename. Flip both sides together when that is scheduled.
    /// </remarks>
    [JsonStringEnumMemberName("Professional")]
    Pro,
}
