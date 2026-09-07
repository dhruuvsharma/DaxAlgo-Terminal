using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Keeps the catalog showing what the registries hold — every authored or installed unit, of both
/// kinds, without a restart.
///
/// <para><b>This is a seam because the same bug shipped twice without it.</b> Each shell used to wire
/// its own copy: read <c>IStrategyKernelRegistry</c>, subscribe to <c>Changed</c>, add a card. The
/// visualizer half of that block was never written in either shell, so
/// <see cref="AuthoredUnitSink"/> would tell an author "Registered visualizer 'X'. Open it from the
/// catalog", the registry would raise the event its own documentation calls "what lets a visualizer
/// authored in Hyperion appear in the catalog without a restart", and no card appeared. The strategy
/// half had had exactly the same hole and had already been fixed once.</para>
///
/// <para>Duplicated wiring inside a shell view-model is also wiring no test can reach — constructing
/// one needs the whole composition root — which is why nothing caught it. Here it is a static call
/// over an <see cref="ObservableCollection{T}"/> and two interfaces, so the behaviour is testable
/// directly and both shells get the same one.</para>
/// </summary>
public static class AuthoredUnitCatalog
{
    /// <summary>
    /// Seeds <paramref name="items"/> from both registries and keeps it in step with them.
    ///
    /// <para>Both registries are optional: an edition that composes only one still binds, and a host
    /// that composes neither gets a no-op rather than a null check at every call site.</para>
    /// </summary>
    /// <param name="items">The catalog the shell binds to. Mutated on the dispatcher.</param>
    /// <param name="kernels">Where <c>IStrategyKernel</c> units land.</param>
    /// <param name="visualizers">Where <c>IVisualizer</c> units land.</param>
    /// <param name="markUnsigned">Called with each id as its card is created. Nobody signed what the
    /// user just authored, so the shells use this to give it the same DEV badge an unsigned plugin's
    /// strategy wears. Taken as an argument rather than reached for, because the shell's set of
    /// unsigned ids is built midway through its constructor and the previous in-line version called
    /// into it from a loop that ran earlier — harmless only for as long as the registry happened to be
    /// empty at start-up.</param>
    /// <param name="dispatch">How to get onto the thread that owns <paramref name="items"/>. Null uses
    /// the WPF dispatcher, which is what a shell wants. A test passes an inline runner: an
    /// <c>Application</c> another test happened to construct would otherwise leave this marshalling to
    /// a dispatcher that has stopped pumping, and a test that hangs is worse than one that fails.</param>
    /// <returns>A handle that stops tracking. The shells hold it for the life of the window.</returns>
    public static IDisposable Bind(
        ObservableCollection<StrategyCatalogItemViewModel> items,
        IStrategyKernelRegistry? kernels,
        IVisualizerRegistry? visualizers,
        Action<string>? markUnsigned = null,
        Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var binding = new Binding(items, kernels, visualizers, markUnsigned, dispatch);
        binding.Attach();
        return binding;
    }

    private sealed class Binding(
        ObservableCollection<StrategyCatalogItemViewModel> items,
        IStrategyKernelRegistry? kernels,
        IVisualizerRegistry? visualizers,
        Action<string>? markUnsigned,
        Action<Action>? dispatch) : IDisposable
    {
        /// <summary>
        /// The ids this binding put in the catalog.
        ///
        /// <para>Removal is scoped to these rather than to "every visualizer card whose id is not in
        /// the registry". The Testing profile seeds a fixture visualizer that has a descriptor and no
        /// registration behind it, and a set difference against the registry would delete it the first
        /// time anything else was authored — a card vanishing because an unrelated unit appeared.</para>
        /// </summary>
        private readonly HashSet<string> _mine = new(StringComparer.Ordinal);

        private bool _disposed;

        internal void Attach()
        {
            Refresh();

            // Both, and at start-up as well as on change. Either half alone is a bug that has already
            // happened: subscribing without seeding means a unit installed before the window opened is
            // invisible until the next registration, and seeding without subscribing means the author
            // is told to look in a catalog that will not show it until a restart.
            if (kernels is not null) kernels.Changed += OnChanged;
            if (visualizers is not null) visualizers.Changed += OnChanged;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (kernels is not null) kernels.Changed -= OnChanged;
            if (visualizers is not null) visualizers.Changed -= OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            // A registration can arrive on any thread — a plugin binder, a compile that finished on a
            // worker — and an ObservableCollection may only be touched by the thread that owns it.
            if (dispatch is not null)
            {
                dispatch(Refresh);
                return;
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                dispatcher.Invoke(Refresh);
            else
                Refresh();
        }

        /// <summary>Re-reads both registries rather than diffing an event that carries nothing: a
        /// registration replaces by id, removal is possible, and this is a handful of entries.</summary>
        private void Refresh()
        {
            if (_disposed) return;

            var live = new HashSet<string>(StringComparer.Ordinal);

            if (kernels is not null)
            {
                foreach (var registration in kernels.All)
                {
                    live.Add(registration.Id);
                    Show(registration.Id, existing => ReferenceEquals(existing.Kernel, registration),
                        () => new StrategyCatalogItemViewModel(registration));
                }
            }

            if (visualizers is not null)
            {
                foreach (var registration in visualizers.All)
                {
                    live.Add(registration.Id);
                    Show(registration.Id, existing => ReferenceEquals(existing.Visualizer, registration.Descriptor),
                        () => new StrategyCatalogItemViewModel(registration.Descriptor));
                }
            }

            foreach (var stale in items.Where(i => _mine.Contains(i.Id) && !live.Contains(i.Id)).ToList())
            {
                _mine.Remove(stale.Id);
                items.Remove(stale);
            }
        }

        /// <summary>
        /// Adds the card, or replaces the one already standing under that id — but leaves an unchanged
        /// one alone.
        ///
        /// <para>The "unchanged" test is what keeps the selection. Every registration re-reads both
        /// registries, so rebuilding each card unconditionally would swap out the object the view is
        /// binding to and the row the user had selected would clear itself because something unrelated
        /// was authored.</para>
        /// </summary>
        private void Show(string id, Func<StrategyCatalogItemViewModel, bool> unchanged, Func<StrategyCatalogItemViewModel> build)
        {
            markUnsigned?.Invoke(id);
            _mine.Add(id);

            var existing = items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
            if (existing is not null && unchanged(existing)) return;

            var card = build();
            if (existing is not null) items[items.IndexOf(existing)] = card;
            else items.Add(card);
        }
    }
}
