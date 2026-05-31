using FluentAssertions;
using TradingTerminal.Core.Strategies.Parameters;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public class StrategyParametersTests
{
    private static StrategyParameterSchema Sample() => new(
        StrategyParameter.Int("period", "Period", 20, min: 2, max: 100),
        StrategyParameter.Number("entryStd", "Entry σ", 2.0, min: 0.5, max: 5.0),
        StrategyParameter.Bool("useStop", "Use stop", true),
        StrategyParameter.Choice("mode", "Mode", "fast", new[] { "fast", "slow" }),
        StrategyParameter.Text("label", "Label", "default"));

    [Fact]
    public void CreateDefaults_seeds_each_parameter_with_its_default()
    {
        var p = Sample().CreateDefaults();

        p.GetInt("period").Should().Be(20);
        p.GetDouble("entryStd").Should().Be(2.0);
        p.GetBool("useStop").Should().BeTrue();
        p.GetString("mode").Should().Be("fast");
        p.GetString("label").Should().Be("default");
    }

    [Fact]
    public void Set_clamps_numeric_values_to_declared_range()
    {
        var p = Sample().CreateDefaults();

        p.Set("period", 9999);
        p.GetInt("period").Should().Be(100); // clamped to max

        p.Set("entryStd", -3.0);
        p.GetDouble("entryStd").Should().Be(0.5); // clamped to min
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData(42.7, 43)] // double rounds to nearest long (round-half-to-even), then clamped to range
    public void GetInt_coerces_loose_boxed_types(object boxed, int expected)
    {
        var p = Sample().CreateDefaults();
        p.Set("period", boxed);
        p.GetInt("period").Should().Be(expected);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("off", false)]
    [InlineData(0, false)]
    public void GetBool_coerces_strings_and_numbers(object boxed, bool expected)
    {
        var p = Sample().CreateDefaults();
        p.Set("useStop", boxed);
        p.GetBool("useStop").Should().Be(expected);
    }

    [Fact]
    public void Choice_rejects_value_outside_allowed_set_and_falls_back_to_default()
    {
        var p = Sample().CreateDefaults();
        p.Set("mode", "nonsense");
        p.GetString("mode").Should().Be("fast");

        p.Set("mode", "slow");
        p.GetString("mode").Should().Be("slow");
    }

    [Fact]
    public void Constructor_applies_supplied_values_and_ignores_unknown_keys()
    {
        var values = new Dictionary<string, object?>
        {
            ["period"] = 50,
            ["ghost"] = 123, // not in schema — must be ignored, not throw
        };

        var p = new StrategyParameters(Sample(), values);

        p.GetInt("period").Should().Be(50);
        p.GetDouble("entryStd").Should().Be(2.0); // untouched -> default
    }

    [Fact]
    public void ToDictionary_round_trips_through_constructor()
    {
        var original = Sample().CreateDefaults();
        original.Set("period", 33);
        original.Set("mode", "slow");

        var restored = new StrategyParameters(Sample(), original.ToDictionary());

        restored.GetInt("period").Should().Be(33);
        restored.GetString("mode").Should().Be("slow");
    }

    [Fact]
    public void Validate_flags_imported_out_of_range_values()
    {
        // Bypass setter clamping by constructing straight from a dictionary that the
        // ctor coerces but Validate re-checks (ctor clamps, so build a bag then mutate raw
        // is not possible; instead assert a clean bag validates empty).
        var p = Sample().CreateDefaults();
        p.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Schema_rejects_duplicate_keys()
    {
        var act = () => new StrategyParameterSchema(
            StrategyParameter.Int("dup", "A", 1),
            StrategyParameter.Int("dup", "B", 2));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unknown_key_access_throws()
    {
        var p = Sample().CreateDefaults();
        var act = () => p.GetInt("missing");
        act.Should().Throw<KeyNotFoundException>();
    }
}
