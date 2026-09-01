using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// An authored unit's instrument parameter needs a picker, not a box asking for a number.
///
/// <para><b>Reported by the user, looking at a generated window:</b> "how will the user select the
/// instrument, there is no instrument selector". The unit was declaring the parameter correctly —
/// <c>StrategyParameter.Instrument("instrument", …)</c> — and the chrome had three editor shapes,
/// checkbox, combo and text box, so an instrument fell through to free text. Its validator then
/// demanded a canonical surrogate id and said so: <i>"Must be an instrument id."</i> Nobody knows
/// their own instrument ids; they are registry surrogates.</para>
///
/// <para>Charts, OrderBook and VolumeFootprint have all used the shared <c>InstrumentPicker</c> since
/// before authored units existed. This is the same list, reachable from the same window.</para>
/// </summary>
public sealed class AuthoredUnitInstrumentPickerTests
{
    private static readonly AuthoredUnitInstrument[] Two =
    [
        new(new InstrumentId(7), "BTCUSDT · Binance"),
        new(new InstrumentId(9), "ES MAR26 · Interactive Brokers"),
    ];

    [Fact]
    public void AnInstrumentParameterOffersTheInstrumentsItWasGiven()
    {
        var row = new AuthoredUnitParameter
        {
            Key = "instrument",
            Kind = ParameterKind.Instrument,
            Instruments = Two,
        };

        Assert.True(row.IsInstrument);
        Assert.False(row.IsFreeText);
        Assert.Equal(2, row.Instruments.Count);
    }

    [Fact]
    public void ThePickerSelectsByTheSameTextTheEditorAlreadyStores()
    {
        // The row's Value stays a string and TryParse stays the one place a value is validated. The
        // picker binds SelectedValuePath to that same text, so choosing a row IS typing its id —
        // no second conversion path to disagree with the first.
        var row = new AuthoredUnitParameter
        {
            Key = "instrument",
            Kind = ParameterKind.Instrument,
            Instruments = Two,
        };

        row.Value = Two[1].IdText;

        Assert.True(row.TryParse(out var parsed));
        Assert.Equal(new InstrumentId(9), parsed);
    }

    [Fact]
    public void WithNoInstrumentsToOfferItStaysATextBox()
    {
        // A preview pane and the verification harness have no registry. Falling back to the old
        // editor keeps them working rather than rendering an empty dropdown that cannot be set.
        var row = new AuthoredUnitParameter { Key = "instrument", Kind = ParameterKind.Instrument };

        Assert.False(row.IsInstrument);
        Assert.True(row.IsFreeText);
    }

    [Fact]
    public void ADeclaredInstrumentParameterReachesTheHostAsAPicker()
    {
        // Through the host, because that is what builds the rows — the presenter being capable of it
        // proves nothing about whether a real unit's schema arrives that way.
        var schema = new StrategyParameterSchema(
        [
            StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(7)),
        ]);

        using var host = new AuthoredUnitHost(
            "Book", _ => true, schema, instruments: Two);

        var row = Assert.Single(host.Presenter.Parameters);
        Assert.True(row.IsInstrument);
        Assert.Equal("7", row.Value);
    }
}
