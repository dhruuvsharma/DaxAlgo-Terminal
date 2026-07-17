using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;
using Xunit;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// Unit tests for <see cref="InstrumentPickerFilter"/> — the shared logic behind every instrument
/// dropdown: an empty term shows the whole (capped) universe, the in-place rebuild that never drops the
/// current selection (which would null the ComboBox and, in tool windows, restart the stream), and the
/// remembered-vs-default initial selection.
///
/// <para>The empty-term rule inverted on 2026-07-17. It used to be "hide the predefined list until you
/// search", which suited a separate search box above a read-only ComboBox. The picker is now a single
/// editable ComboBox, so an empty box must show the list — otherwise clicking the arrow shows one row
/// and the control reads as broken.</para>
/// </summary>
public sealed class InstrumentPickerFilterTests
{
    private static SignalInstrument Inst(string symbol) =>
        new($"{symbol} — {symbol} name", "Stock", Contract.UsStock(symbol));

    [Fact]
    public void Visible_with_no_search_term_shows_the_whole_universe()
    {
        var all = new[] { Inst("AAPL"), Inst("MSFT"), Inst("NVDA") };
        var selected = all[1];

        InstrumentPickerFilter.Visible(all, "", selected, 500)
            .Should().Equal(all, "an empty editable ComboBox must let the user browse the universe");

        // Whitespace counts as "no term" too.
        InstrumentPickerFilter.Visible(all, "   ", selected, 500)
            .Should().Equal(all);
    }

    [Fact]
    public void Visible_with_no_selection_and_no_term_still_shows_the_universe()
    {
        var all = new[] { Inst("AAPL"), Inst("MSFT") };

        InstrumentPickerFilter.Visible(all, "", (SignalInstrument?)null, 500)
            .Should().Equal(all);
    }

    [Fact]
    public void Visible_treats_the_selections_own_name_as_browsing()
    {
        // The editable ComboBox writes the picked row's name into the text box, so after a selection
        // the term is never empty. Without this rule, clicking the arrow would drop open onto a
        // one-row list — the exact "the dropdown only shows what I already picked" complaint.
        var all = new[] { Inst("AAPL"), Inst("MSFT"), Inst("NVDA") };
        var selected = all[1];

        InstrumentPickerFilter.Visible(all, selected.DisplayName, selected, 500)
            .Should().Equal(all);

        // ...but a real search term still filters.
        InstrumentPickerFilter.Visible(all, "NVDA", selected, 500)
            .Should().Contain(all[2]).And.NotContain(all[0]);
    }

    [Fact]
    public void Visible_caps_an_empty_term_too()
    {
        // A broker universe runs to thousands of symbols, so the empty-term path has to honour the cap
        // as strictly as the search path does.
        var all = Enumerable.Range(0, 2000).Select(i => Inst($"SYM{i:D4}")).ToArray();

        InstrumentPickerFilter.Visible(all, "", selected: null, cap: 500).Should().HaveCount(500);
    }

    [Fact]
    public void Visible_with_a_term_filters_by_display_name_and_force_includes_the_selection()
    {
        var all = new[] { Inst("AAPL"), Inst("MSFT"), Inst("NVDA") };
        var selected = all[2]; // NVDA — not a match for "ms"

        var shown = InstrumentPickerFilter.Visible(all, "ms", selected, 500);

        shown.Should().Contain(all[1]);       // MSFT matched the term
        shown.Should().Contain(selected);     // NVDA force-included so the picker never blanks
        shown.Should().NotContain(all[0]);    // AAPL didn't match
    }

    [Fact]
    public void Visible_respects_the_cap()
    {
        var all = Enumerable.Range(0, 50).Select(i => Inst($"SYM{i:D2}")).ToArray();

        InstrumentPickerFilter.Visible(all, "SYM", selected: null, cap: 10)
            .Should().HaveCount(10);
    }

    [Fact]
    public void Visible_multi_pin_shows_only_the_pinned_selections_when_no_term()
    {
        var all = new[] { Inst("SPY"), Inst("QQQ"), Inst("IWM") };
        var pinned = new SignalInstrument?[] { all[0], all[1], null };

        InstrumentPickerFilter.Visible(all, "", pinned, 500)
            .Should().Equal(all[0], all[1]); // nulls dropped, both selections kept
    }

    [Fact]
    public void Apply_rebuilds_in_place_without_ever_removing_the_selection()
    {
        var sel = Inst("SPY");
        var a = Inst("QQQ");
        var b = Inst("IWM");
        var target = new ObservableCollection<SignalInstrument> { sel };

        // User types: list expands to matches with the selection force-included.
        InstrumentPickerFilter.Apply(target, new List<SignalInstrument> { sel, a, b });
        target.Should().Equal(sel, a, b);
        target[0].Should().BeSameAs(sel); // same instance — no re-creation, no selection churn

        // User clears the search: list collapses back to just the selection.
        InstrumentPickerFilter.Apply(target, new List<SignalInstrument> { sel });
        target.Should().Equal(sel);
        target[0].Should().BeSameAs(sel);
    }

    [Fact]
    public void Apply_reorders_to_match_desired()
    {
        var a = Inst("A");
        var b = Inst("B");
        var c = Inst("C");
        var target = new ObservableCollection<SignalInstrument> { a, b, c };

        InstrumentPickerFilter.Apply(target, new List<SignalInstrument> { c, a });

        target.Should().Equal(c, a); // b removed, remainder reordered
    }

    [Fact]
    public void InitialSelection_falls_back_to_the_default_when_nothing_is_remembered()
    {
        var all = new[] { Inst("AAPL"), Inst("MSFT") };
        var unusedKey = "test.no-such-key." + Guid.NewGuid();

        var initial = InstrumentPickerFilter.InitialSelection(unusedKey, all, () => all[1]);

        initial.Should().BeSameAs(all[1]);
    }
}
