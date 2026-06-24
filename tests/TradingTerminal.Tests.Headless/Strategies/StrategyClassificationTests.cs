using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

/// <summary>
/// Guards the classification defaults on <see cref="ITradingStrategy"/> and the broker-capability
/// matrix that backs <see cref="ITradingStrategy.SupportedBrokers"/> — the data behind the
/// asset-class / scope / broker pills in the Strategies pane.
/// </summary>
public sealed class StrategyClassificationTests
{
    [Fact]
    public void Defaults_are_asset_agnostic_single_and_broker_agnostic()
    {
        ITradingStrategy s = new BaselineStrategy();

        s.AssetClasses.Should().BeEmpty();                       // ANY ASSET
        s.AssetScope.Should().Be(StrategyAssetScope.SingleAsset);
        s.SupportedBrokers.Should().BeEmpty();                   // ANY BROKER (L1 + Bars only)
    }

    [Fact]
    public void Tape_strategy_supports_only_tape_capable_brokers()
    {
        ITradingStrategy s = new TapeStrategy();

        s.SupportedBrokers.Should().BeEquivalentTo(StrategyBrokerCapability.TapeBrokers);
        s.SupportedBrokers.Should().Contain(BrokerKind.InteractiveBrokers)
            .And.Contain(BrokerKind.Binance)
            .And.Contain(BrokerKind.IronBeam);
        s.SupportedBrokers.Should().NotContain(BrokerKind.Alpaca);
    }

    [Fact]
    public void Depth_strategy_supports_depth_capable_brokers()
    {
        ITradingStrategy s = new DepthStrategy();

        s.SupportedBrokers.Should().BeEquivalentTo(StrategyBrokerCapability.DepthBrokers);
        s.SupportedBrokers.Should().Contain(BrokerKind.CTrader);
        s.SupportedBrokers.Should().NotContain(BrokerKind.NinjaTrader);
    }

    [Fact]
    public void Overrides_flow_through_to_the_interface()
    {
        ITradingStrategy s = new MultiAssetStrategy();

        s.AssetClasses.Should().Equal(AssetClass.Index, AssetClass.Equity);
        s.AssetScope.Should().Be(StrategyAssetScope.MultiAsset);
    }

    [Theory]
    [InlineData(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars, 0)]
    [InlineData(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape, 3)]
    [InlineData(StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth, 9)]
    public void Capability_matrix_maps_requirement_to_broker_count(StrategyDataRequirement req, int expected)
        => StrategyBrokerCapability.ForRequirement(req).Should().HaveCount(expected);

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────
    private sealed class BaselineStrategy : ITradingStrategy
    {
        public string Id => "test.baseline";
        public string DisplayName => "Baseline";
        public string Description => "L1 + Bars only.";
    }

    private sealed class TapeStrategy : ITradingStrategy
    {
        public string Id => "test.tape";
        public string DisplayName => "Tape";
        public string Description => "Needs the trade tape.";
        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;
    }

    private sealed class DepthStrategy : ITradingStrategy
    {
        public string Id => "test.depth";
        public string DisplayName => "Depth";
        public string Description => "Needs L2 depth.";
        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth;
    }

    private sealed class MultiAssetStrategy : ITradingStrategy
    {
        public string Id => "test.multi";
        public string DisplayName => "Multi";
        public string Description => "Index composite over equity constituents.";
        public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Index, AssetClass.Equity };
        public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
    }
}
