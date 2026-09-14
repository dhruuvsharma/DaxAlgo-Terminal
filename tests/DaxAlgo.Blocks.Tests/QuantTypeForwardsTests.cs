using DaxAlgo.Sdk;
using FluentAssertions;
using Xunit;

namespace DaxAlgo.Blocks.Tests;

/// <summary>
/// The quant library moved out of DaxAlgo.Sdk. Anything compiled against the old assembly names its
/// types as <c>[DaxAlgo.Sdk]DaxAlgo.Sdk.Quant.X</c>, so every one of them must still be forwarded.
/// </summary>
public sealed class QuantTypeForwardsTests
{
    [Fact]
    public void Every_quant_type_is_still_reachable_through_the_sdk()
    {
        var quant = typeof(DaxAlgo.Sdk.Quant.Num).Assembly.GetExportedTypes()
            .Where(type => !type.IsNested)
            .ToArray();

        var forwarded = typeof(IStrategyKernel).Assembly.GetForwardedTypes();

        quant.Should().NotBeEmpty();
        foreach (var type in quant)
            forwarded.Should().Contain(type, "a unit compiled against DaxAlgo.Sdk still names {0} there", type.Name);
    }

    [Fact]
    public void The_namespace_did_not_move()
    {
        typeof(DaxAlgo.Sdk.Quant.Ema).Namespace.Should().Be("DaxAlgo.Sdk.Quant");
        typeof(DaxAlgo.Sdk.Quant.Ema).Assembly.GetName().Name.Should().Be("DaxAlgo.Quant");
    }
}
