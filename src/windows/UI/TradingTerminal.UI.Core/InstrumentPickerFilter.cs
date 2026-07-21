using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace TradingTerminal.UI;

/// <summary>
/// Shared logic for the instrument dropdowns used across every strategy / tool / chart / ML window.
///
/// <para>Encodes the "hide the list until you search" behaviour: with no search term the picker shows
/// only the currently-selected row — never the full predefined universe — and typing reveals matches.
/// Paired with <see cref="LastInstrumentStore"/>, a reopened window restores the instrument last used
/// there rather than a hardcoded default.</para>
///
/// <para><see cref="Apply{T}"/> rebuilds the bound collection in place without ever transiently dropping
/// the selected row. A plain Clear()+Add() (or reassigning the collection) momentarily removes the
/// selected item, which nulls the ComboBox selection — and in tool windows that (re)start their stream
/// on selection change, that spurious null would stop and restart the feed. The in-place diff avoids
/// it entirely.</para>
/// </summary>
public static class InstrumentPickerFilter
{
    private static readonly ConditionalWeakTable<object, ApplyState> ApplyStates = new();

    private sealed class ApplyState
    {
        public bool IsApplying { get; set; }
        public object? PendingDesired { get; set; }
    }

    /// <summary>Rows to show for a <see cref="SignalInstrument"/> picker — see <see cref="Visible{T}"/>.</summary>
    public static List<SignalInstrument> Visible(
        IReadOnlyList<SignalInstrument> all, string? term, SignalInstrument? selected, int cap)
        => Visible(all, term, selected, cap, i => i.DisplayName);

    /// <summary>
    /// The rows a picker should display. An empty/whitespace <paramref name="term"/> yields the whole
    /// universe (capped) so opening the dropdown lets the user browse what their broker offers; a
    /// non-empty term yields case-insensitive <paramref name="name"/> matches. Either way the row count
    /// is capped at <paramref name="cap"/> and the selection is force-included, so the picker never
    /// blanks mid-filter (which is what keeps a bound ComboBox from dropping its selection).
    ///
    /// <para>Until 2026-07-17 an empty term showed <i>only</i> the selection — "hide until you search".
    /// That made sense beside a separate search box, but the picker is now a single editable ComboBox:
    /// clicking the arrow has to show the list, or the control reads as broken.</para>
    ///
    /// <para>A term equal to the selected row's own name counts as empty. That isn't a special case for
    /// its own sake: an editable ComboBox <i>puts</i> the picked row's name in the text box, so after
    /// any selection the term is never empty, and the arrow would otherwise drop open onto a
    /// single-row list. Typing anything else filters normally.</para>
    /// </summary>
    public static List<T> Visible<T>(
        IReadOnlyList<T> all, string? term, T? selected, int cap, Func<T, string> name) where T : class
    {
        term = term?.Trim() ?? string.Empty;
        var browsing = term.Length == 0
            || (selected is not null && string.Equals(term, name(selected), StringComparison.OrdinalIgnoreCase));

        var matches = browsing
            ? all
            : all.Where(i => name(i).Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = matches.Take(cap).ToList();
        if (selected is not null && !shown.Contains(selected)) shown.Insert(0, selected);
        return shown;
    }

    /// <summary>Multi-selection form of <see cref="Visible{T}"/> for a shared list backing more than one
    /// dropdown (e.g. the Kalman pairs picker's primary + second instrument). With no search term it
    /// shows exactly the <paramref name="pinned"/> selections; typing filters the universe with all pins
    /// force-included so none of the dropdowns blanks out.</summary>
    public static List<SignalInstrument> Visible(
        IReadOnlyList<SignalInstrument> all, string? term, IReadOnlyList<SignalInstrument?> pinned, int cap)
    {
        term = term?.Trim() ?? string.Empty;
        var pins = pinned.Where(p => p is not null).Cast<SignalInstrument>().Distinct().ToList();
        if (term.Length == 0) return pins;

        var shown = all.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
                       .Take(cap).ToList();
        foreach (var p in pins)
            if (!shown.Contains(p)) shown.Insert(0, p);
        return shown;
    }

    /// <summary>
    /// Rebuilds <paramref name="target"/> to match <paramref name="desired"/> in place — first removing
    /// rows that dropped out, then inserting/moving the rest into order. Because <see cref="Visible{T}"/>
    /// always keeps the selected row in <paramref name="desired"/>, the selected item is never removed,
    /// so the bound ComboBox keeps its selection (no spurious null, no stream-restart flicker).
    /// </summary>
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        // ObservableCollection notifications are synchronous. An editable ComboBox can update its
        // search text while an item is inserted, which immediately calls Apply again for the same
        // collection. Let the active pass finish and then apply only the latest requested snapshot;
        // otherwise the nested pass can shrink the list beneath the outer pass's next Insert index.
        var state = ApplyStates.GetOrCreateValue(target);
        // Broker discovery can surface equal rows through more than one registration path. Keep the
        // first occurrence: Move requires a destination strictly below Count, and a duplicate desired
        // item otherwise makes IndexOf find the earlier copy while i advances past the list's end.
        var next = desired.Distinct().ToArray();
        lock (state)
        {
            if (state.IsApplying)
            {
                state.PendingDesired = next;
                return;
            }
            state.IsApplying = true;
        }

        try
        {
            while (true)
            {
                ApplyCore(target, next);

                lock (state)
                {
                    if (state.PendingDesired is T[] pending)
                    {
                        state.PendingDesired = null;
                        next = pending;
                        continue;
                    }

                    state.IsApplying = false;
                    return;
                }
            }
        }
        catch
        {
            lock (state)
            {
                state.IsApplying = false;
                state.PendingDesired = null;
            }
            throw;
        }
    }

    private static void ApplyCore<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        var desiredSet = new HashSet<T>(desired);
        var retained = new HashSet<T>();
        for (var i = 0; i < target.Count;)
        {
            var item = target[i];
            if (!desiredSet.Contains(item) || !retained.Add(item)) target.RemoveAt(i);
            else i++;
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            int cur = target.IndexOf(item);
            if (cur < 0) target.Insert(i, item);
            else if (cur != i) target.Move(cur, i);
        }
    }

    /// <summary>The instrument remembered under <paramref name="key"/> resolved against
    /// <paramref name="all"/> by canonical symbol, or null when nothing is remembered or it isn't in the
    /// current universe. Used to restore the last selected instrument when a window reopens.</summary>
    public static SignalInstrument? Remembered(string key, IReadOnlyList<SignalInstrument> all)
        => Remembered(key, all, i => i.Contract.Symbol);

    /// <summary>Generic form of <see cref="Remembered(string,IReadOnlyList{SignalInstrument})"/> for
    /// pickers whose row type isn't <see cref="SignalInstrument"/> (e.g. the Charts window's
    /// <c>TradableInstrument</c>). <paramref name="symbolOf"/> extracts the canonical symbol.</summary>
    public static T? Remembered<T>(string key, IReadOnlyList<T> all, Func<T, string> symbolOf) where T : class
        => LastInstrumentStore.Load(key) is { } sym ? all.FirstOrDefault(i => symbolOf(i) == sym) : null;

    /// <summary>
    /// The selection to show when a picker first populates. If an instrument is remembered under
    /// <paramref name="key"/>, it is resolved against <paramref name="all"/> — returning it, or
    /// <c>null</c> when it isn't in this (possibly pre-broker) universe yet, so a subsequent
    /// broker-universe load can restore it instead of a default silently overwriting it. Only when
    /// *nothing* is remembered is <paramref name="defaultSelection"/> (the window's first-run default)
    /// returned. Because it never substitutes a default for an unresolved-but-remembered pick, callers
    /// may safely persist on selection change without a default clobbering the remembered value.
    /// </summary>
    public static SignalInstrument? InitialSelection(
        string key, IReadOnlyList<SignalInstrument> all, Func<SignalInstrument?> defaultSelection)
        => InitialSelection(key, all, i => i.Contract.Symbol, defaultSelection);

    /// <summary>Generic form of <see cref="InitialSelection(string,IReadOnlyList{SignalInstrument},Func{SignalInstrument})"/>.</summary>
    public static T? InitialSelection<T>(
        string key, IReadOnlyList<T> all, Func<T, string> symbolOf, Func<T?> defaultSelection) where T : class
        => LastInstrumentStore.Load(key) is { } sym
            ? all.FirstOrDefault(i => symbolOf(i) == sym)
            : defaultSelection();
}
