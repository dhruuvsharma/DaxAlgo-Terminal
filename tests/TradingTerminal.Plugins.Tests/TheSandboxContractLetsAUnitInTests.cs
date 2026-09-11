using System.IO;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A unit the authoring compiler accepts must be a unit the loader will run.
///
/// <para><b>They disagreed, and every authored unit in existence was quarantined for it.</b>
/// <c>RoslynStrategyCompiler</c> injects <c>TradingTerminal.Core.Strategies</c> and
/// <c>TradingTerminal.Core.Strategies.Parameters</c> as global usings — they hold
/// <c>StrategyDataRequirement</c> and <c>StrategyParameterSchema</c>, which BOTH sandbox contracts
/// require as members — and the sandbox policy scan denied that whole prefix as host surface. So a unit
/// compiled clean, verified clean, registered, installed, and was then refused at startup with
/// "accesses host services outside the sandbox contract".</para>
///
/// <para>Reported from real use: six units built and installed, all six quarantined on the next start.
/// The shipped samples fail the same way, which is what makes this a contract bug rather than a bad
/// unit — nothing could ever have loaded.</para>
///
/// <para>The samples are the fixture on purpose. They are compiled by this solution, they implement the
/// contracts the way the documentation says to, and they are the closest thing to "a correct unit" that
/// exists. A scan that refuses them is refusing the contract.</para>
/// </summary>
public sealed class TheSandboxContractLetsAUnitInTests(ITestOutputHelper output)
{
    /// <summary>The shipped sample units, as built beside this test assembly.</summary>
    private static string SamplesDirectory => AppContext.BaseDirectory;

    private static string SamplesAssembly =>
        Path.Combine(SamplesDirectory, "DaxAlgo.Sandbox.Samples.dll");

    [Fact]
    public void The_shipped_samples_pass_the_sandbox_scan()
    {
        File.Exists(SamplesAssembly).Should().BeTrue(
            "the samples are a project reference of this test assembly");

        var report = PluginPolicyScanner.Scan(
            SamplesDirectory, declaredPermissions: null, PluginScanProfile.Sandbox);

        var blocking = report.Findings
            .Where(f => f.Severity == PluginScanSeverity.Block
                        && f.Assembly.Contains("Sandbox.Samples", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var finding in blocking) output.WriteLine($"{finding.Rule}: {finding.Detail}");

        blocking.Should().BeEmpty(
            "a unit written exactly as the SDK documents must be loadable; anything else means the "
            + "compiler and the loader disagree about what the contract is");
    }

    [Theory]
    [InlineData("TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema")]
    [InlineData("TradingTerminal.Core.Strategies.Parameters.StrategyParameter")]
    [InlineData("TradingTerminal.Core.Strategies.StrategyDataRequirement")]
    public void The_types_the_contract_makes_mandatory_are_not_host_surface(string type)
    {
        // These are not "access to host services" in any sense a user could act on: they are the return
        // types of members the interfaces REQUIRE. A unit cannot implement IStrategyKernel or
        // IVisualizer without naming them, so denying them denies the contract itself.
        PluginPolicyScanner.IsSandboxContractType(type).Should().BeTrue();
    }

    [Theory]
    [InlineData("TradingTerminal.Core.Strategies.IStrategyRegistry")]
    [InlineData("TradingTerminal.Core.Strategies.StrategyCatalogEntry")]
    [InlineData("TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler")]
    public void The_rest_of_that_namespace_is_still_host_surface(string type)
    {
        // The guard against over-reading the fix. TradingTerminal.Core.Strategies also holds the
        // catalog, the registry and the authoring compiler — a sandboxed unit reaching for those is
        // exactly what the rule exists to stop, and widening the allowance to the whole namespace to
        // let two types through would have thrown that away.
        PluginPolicyScanner.IsSandboxContractType(type).Should().BeFalse();
    }
}
