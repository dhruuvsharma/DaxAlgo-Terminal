using FluentAssertions;
using TradingTerminal.Core.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// The sandbox policy is a security boundary: it must deny by default. These assertions are the
/// deterministic invariants the verifier/Stop hooks rely on — if one breaks, isolation has been
/// weakened.
/// </summary>
public sealed class SandboxPolicyTests
{
    [Fact]
    public void Default_policy_denies_all_egress_and_host_mounts()
    {
        var policy = new SandboxPolicy();

        policy.EgressAllowlist.Should().BeEmpty("no network egress by default");
        policy.IsNetworkDenied.Should().BeTrue("empty allowlist must map to --network none");
        policy.DeniedHostPaths.Should().BeEmpty("no host paths are bind-mounted by default");
    }

    [Fact]
    public void DenyAll_uses_strict_quota()
    {
        var policy = SandboxPolicy.DenyAll;

        policy.IsNetworkDenied.Should().BeTrue();
        policy.EgressAllowlist.Should().BeEmpty();
        policy.Quota.Should().Be(SandboxQuota.Strict);
    }

    [Fact]
    public void Strict_quota_has_bounded_caps()
    {
        var q = SandboxQuota.Strict;

        q.Cpus.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(2.0);
        q.MemoryMb.Should().BeGreaterThan(0);
        q.PidsLimit.Should().BeGreaterThan(0);
        q.DiskMb.Should().BeGreaterThan(0);
        q.WallClock.Should().BeGreaterThan(TimeSpan.Zero, "a wall-clock timeout must always be set");
    }

    [Fact]
    public void Adding_an_egress_host_lifts_network_denial_only_for_that_scope()
    {
        var scoped = new SandboxPolicy(
            EgressAllowlist: new[] { "data.example.org" },
            DeniedHostPaths: Array.Empty<string>(),
            Quota: SandboxQuota.Strict);

        scoped.IsNetworkDenied.Should().BeFalse("an explicit allowlist entry scopes egress");
        scoped.EgressAllowlist.Should().ContainSingle().Which.Should().Be("data.example.org");
    }
}
