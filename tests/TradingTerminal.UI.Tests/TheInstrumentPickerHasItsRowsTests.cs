using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The instrument row renders a picker, or it renders a demand nobody can satisfy.
///
/// <para><b>Reported twice, from real use: "the instrument selector is broken."</b> The first time it
/// looked like the unit's fault — a parameter declared and never read — and it was not: reading it
/// changed nothing, because the defect is in the shell.
/// <c>MainWindowViewModel.SelectableInstruments</c> started the universe load and returned the cache
/// without waiting for it, and on the first call that cache is empty. The picker only appears when it
/// has rows, so the first authored unit opened in a session fell through to a FREE TEXT BOX whose
/// validator then asked for a canonical surrogate id — "Must be an instrument id" — which nobody knows
/// for their own instruments. Intermittent exactly as a race is: a window opened later inherited the
/// list the first one's load had finally filled.</para>
///
/// <para>This is the rule the shell has to keep, stated where it can be checked: rows present means a
/// picker, no rows means the text box, and the shell's job is to have rows.</para>
/// </summary>
public sealed class TheInstrumentPickerHasItsRowsTests
{
    private static AuthoredUnitParameter Row(params AuthoredUnitInstrument[] instruments) =>
        new()
        {
            Key = "instrument",
            Kind = ParameterKind.Instrument,
            Value = "1",
            Instruments = instruments,
        };

    private static AuthoredUnitInstrument Instrument(int id, string name) =>
        new(new InstrumentId(id),
            new SignalInstrument(name, "Futures", new Contract(name, "FUT", "CME", "USD", "CME")),
            BrokerKind.Simulated);

    [Fact]
    public void With_rows_it_is_a_picker()
    {
        var row = Row(Instrument(1, "ESZ6"), Instrument(2, "NQZ6"));

        Assert.True(row.IsInstrument);
        Assert.False(row.IsFreeText, "a picker and a text box are alternatives, not a pair");
    }

    [Fact]
    public void With_no_rows_it_falls_back_to_the_text_box()
    {
        // Which is correct as a FALLBACK and unusable as the normal case: the validator behind it wants
        // a canonical surrogate id. The bug was never this rule — it was the shell handing it an empty
        // list every first time.
        var row = Row();

        Assert.False(row.IsInstrument);
        Assert.True(row.IsFreeText);
    }

    [Fact]
    public void Picking_a_row_writes_the_id_the_feed_will_publish_under()
    {
        // The whole point of the picker: the user chooses a symbol and the unit receives the canonical
        // id its broker's feed actually publishes. A picker that wrote the display name back would look
        // fine and subscribe to nothing.
        var esz6 = Instrument(7, "ESZ6");
        var row = Row(esz6, Instrument(9, "NQZ6"));

        row.SeedInstruments();
        row.SelectedInstrument = esz6.Instrument;

        Assert.Equal("7", row.Value);
    }

    [Fact]
    public void The_row_opens_on_whatever_the_unit_already_declared()
    {
        // Reopening a saved unit must show the instrument it was saved with, not the first in the list.
        var row = Row(Instrument(1, "ESZ6"), Instrument(2, "NQZ6"));
        row.Value = "2";

        row.SeedInstruments();

        Assert.Equal("NQZ6", row.SelectedInstrument?.DisplayName);
    }
}
