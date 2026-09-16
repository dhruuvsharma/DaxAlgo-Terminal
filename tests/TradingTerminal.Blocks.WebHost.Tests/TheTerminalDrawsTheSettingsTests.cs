using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.Blocks.WebHost.Tests;

/// <summary>
/// The settings panel is the TERMINAL'S page, not the unit's: same rows, same validation, same Apply as
/// the expander it replaces, and an instrument picker no author can get wrong.
///
/// <para>Driven through the bridge rather than a browser, because what is worth pinning is the contract —
/// what the page is told, and what it is allowed to ask for. That the page itself loads is asserted with
/// a real WebView2 in <see cref="TheUnitWindowHostsThePageTests"/>.</para>
/// </summary>
public sealed class TheTerminalDrawsTheSettingsTests
{
    private static AuthoredUnitHost Unit(
        StrategyParameterSchema schema,
        Func<IReadOnlyDictionary<string, object?>, Task>? apply = null,
        IReadOnlyList<AuthoredUnitInstrument>? instruments = null) =>
        new("Unit", _ => false, schema, values: null, log: null, hasBook: false,
            apply: apply ?? (_ => Task.CompletedTask), instruments: instruments);

    private static JsonElement State(SettingsBridge bridge) => JsonDocument.Parse(bridge.StateJson()).RootElement;

    [Fact]
    public void ThePageIsToldEveryRowTheUnitDeclared()
    {
        using var host = Unit(new StrategyParameterSchema(
            StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500, unit: "bars"),
            StrategyParameter.Choice("side", "Side", "Both", ["Both", "Long", "Short"])));

        var state = State(new SettingsBridge(host.Presenter));
        var rows = state.GetProperty("parameters");

        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("key").GetString().Should().Be("lookback");
        rows[0].GetProperty("label").GetString().Should().Be("Look-back");
        rows[0].GetProperty("kind").GetString().Should().Be("Integer");
        rows[0].GetProperty("value").GetString().Should().Be("20");
        rows[0].GetProperty("unit").GetString().Should().Be("bars");
        rows[0].GetProperty("rangeHint").GetString().Should().Contain("2");
        rows[1].GetProperty("choices").EnumerateArray().Select(c => c.GetString()).Should().Equal("Both", "Long", "Short");
        state.GetProperty("canEdit").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void AnEditFromThePageGoesThroughTheSameValidationAsTheExpander()
    {
        using var host = Unit(new StrategyParameterSchema(
            StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500)));
        var bridge = new SettingsBridge(host.Presenter);

        bridge.Handle("set", """{"key":"lookback","value":"900"}""");

        var row = host.Presenter.Parameters.Single();
        row.Value.Should().Be("900");
        row.TryParse(out _).Should().BeFalse();
        State(bridge).GetProperty("parameters")[0].GetProperty("error").GetString()
            .Should().Contain("at most 500", "the page shows the same message the expander does");
    }

    [Fact]
    public void ApplyFromThePageRestartsTheUnitWithTheEditedValues()
    {
        IReadOnlyDictionary<string, object?>? applied = null;
        using var host = Unit(
            new StrategyParameterSchema(StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500)),
            apply: values => { applied = values; return Task.CompletedTask; });
        var bridge = new SettingsBridge(host.Presenter);

        bridge.Handle("set", """{"key":"lookback","value":"34"}""");
        State(bridge).GetProperty("isDirty").GetBoolean().Should().BeTrue();

        bridge.Handle("apply", "null");

        applied.Should().NotBeNull();
        applied!["lookback"].Should().Be(34L);
    }

    [Fact]
    public void AnInvalidRowStopsTheApplyRatherThanApplyingHalfOfIt()
    {
        var applies = 0;
        using var host = Unit(
            new StrategyParameterSchema(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500),
                StrategyParameter.Number("threshold", "Threshold", 1.5)),
            apply: _ => { applies++; return Task.CompletedTask; });
        var bridge = new SettingsBridge(host.Presenter);

        bridge.Handle("set", """{"key":"lookback","value":"0"}""");
        bridge.Handle("set", """{"key":"threshold","value":"2.5"}""");
        bridge.Handle("apply", "null");

        applies.Should().Be(0, "every row is checked before any is applied");
        State(bridge).GetProperty("status").GetString().Should().Contain("not valid");
    }

    [Fact]
    public void ResetPutsTheRowsBackToWhatTheUnitIsRunningWith()
    {
        using var host = Unit(new StrategyParameterSchema(StrategyParameter.Int("lookback", "Look-back", 20)));
        var bridge = new SettingsBridge(host.Presenter);

        bridge.Handle("set", """{"key":"lookback","value":"77"}""");
        bridge.Handle("reset", "null");

        host.Presenter.Parameters.Single().Value.Should().Be("20");
        State(bridge).GetProperty("isDirty").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void TheInstrumentPickerOffersRowsAndSearchNarrowsThem()
    {
        // The picker the terminal owns, and the reason it does: a generated one could not pick an
        // instrument at all — the id is a registry surrogate nobody knows by heart.
        var instruments = new[]
        {
            Instrument(1, "ES · S&P 500 future", "Futures"),
            Instrument(2, "BTCUSDT", "Crypto"),
            Instrument(3, "AAPL", "Equity"),
        };

        using var host = Unit(
            new StrategyParameterSchema(StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1))),
            instruments: instruments);
        var bridge = new SettingsBridge(host.Presenter);

        var offered = State(bridge).GetProperty("parameters")[0].GetProperty("instruments");
        offered.GetArrayLength().Should().Be(3);
        offered[0].GetProperty("id").GetString().Should().Be("1");
        offered[0].GetProperty("name").GetString().Should().Be("ES · S&P 500 future");

        bridge.Handle("search", """{"key":"instrument","value":"btc"}""");

        var narrowed = State(bridge).GetProperty("parameters")[0];
        narrowed.GetProperty("search").GetString().Should().Be("btc");

        var names = narrowed.GetProperty("instruments").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToArray();
        names.Should().Contain("BTCUSDT").And.NotContain("AAPL");
        names.Should().Contain("ES · S&P 500 future",
            "the picker keeps the row the unit is running on visible, whatever the search says");

        bridge.Handle("set", """{"key":"instrument","value":"2"}""");
        host.Presenter.Parameters.Single().Value.Should().Be("2");
    }

    [Fact]
    public void ATopicThePanelDoesNotKnowIsIgnoredRatherThanThrowing()
    {
        using var host = Unit(new StrategyParameterSchema(StrategyParameter.Int("lookback", "Look-back", 20)));
        var bridge = new SettingsBridge(host.Presenter);

        var act = () =>
        {
            bridge.Handle("nonsense", """{"key":"lookback","value":"3"}""");
            bridge.Handle("set", "not json");
        };

        act.Should().NotThrow();
        host.Presenter.Parameters.Single().Value.Should().Be("20");
    }

    private static AuthoredUnitInstrument Instrument(int id, string name, string category) =>
        new(new InstrumentId(id),
            new TradingTerminal.UI.SignalInstrument(name, category, Contract.UsStock(name)),
            BrokerKind.Simulated);
}
