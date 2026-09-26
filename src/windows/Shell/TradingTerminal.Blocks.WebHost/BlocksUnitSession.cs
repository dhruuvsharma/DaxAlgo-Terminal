using System.Windows;
using DaxAlgo.Blocks;
using TradingTerminal.Blocks.Runtime;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.UI.Controls.Render;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>
/// An open Blocks unit: the runtime running it, its page, and the same window chrome every authored unit
/// gets — run state, settings with the instrument picker, the book for a strategy, the unit's log.
///
/// <para><b>The page is the picture; everything around it is the terminal's.</b> The author decides how
/// the unit looks, and no unit can forget the settings panel, hide its book, or lose its log, because
/// none of those are the author's to draw.</para>
///
/// <para>Composed here rather than inside each shell's view-model, because that is where the widget
/// SDK's version of this wiring sat, and every seam it missed shipped missing in both shells.</para>
/// </summary>
public sealed class BlocksUnitSession : IAsyncDisposable
{
    private readonly BlocksUnitRegistration _registration;
    private readonly string _pageRoot;
    private int _disposed;

    private BlocksUnitSession(
        BlocksUnitRegistration registration, BlocksUnitRuntime runtime, AuthoredUnitHost unit, WebUnitView? page, string pageRoot)
    {
        _registration = registration;
        Runtime = runtime;
        Unit = unit;
        Page = page;
        _pageRoot = pageRoot;
    }

    /// <summary>The runtime running the unit.</summary>
    public BlocksUnitRuntime Runtime { get; }

    /// <summary>The window chrome; bind an <c>AuthoredUnitView</c> to its presenter.</summary>
    public AuthoredUnitHost Unit { get; }

    /// <summary>The unit's page, or null when it has none or WebView2 is not installed.</summary>
    public WebUnitView? Page { get; }

    /// <summary>The terminal's settings panel beside it, or null when the unit declares nothing to set.</summary>
    public SettingsPanelView? Settings { get; private init; }

    /// <summary>A strategy's book as the execution engine copies it, or null for a visualizer. A shell attaches it to
    /// the execution books that name the strategy.</summary>
    public BlocksModelBook? ModelBook { get; private init; }

    /// <summary>Builds the session. Nothing runs until <see cref="StartAsync"/>.</summary>
    /// <param name="registration">The unit.</param>
    /// <param name="host">What the terminal lends it: data, clock, log, feeds, state.</param>
    /// <param name="log">The app-wide activity log; the window shows this unit's slice.</param>
    /// <param name="instruments">What an instrument setting offers.</param>
    /// <param name="viewOptions">WebView2 profile options; the terminal's default when null.</param>
    /// <param name="pageRoot">Where page files are written; the terminal's default when null.</param>
    public static BlocksUnitSession Create(
        BlocksUnitRegistration registration,
        BlocksHost host,
        InMemoryLogSink? log = null,
        IReadOnlyList<AuthoredUnitInstrument>? instruments = null,
        WebUnitViewOptions? viewOptions = null,
        string? pageRoot = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(host);

        // Read off a throwaway instance, as the widget SDK's windows do: the running one belongs to the
        // unit thread, and the settings panel is built before it exists.
        var info = SafeInfo(registration);
        var title = string.IsNullOrWhiteSpace(info?.Name) ? registration.Id : info!.Name;
        var schema = info?.Settings is { Count: > 0 } declared ? new StrategyParameterSchema(declared) : StrategyParameterSchema.Empty;

        var page = registration.PageFiles.Count > 0 && WebUnitView.RuntimeAvailable ? new WebUnitView(viewOptions) : null;
        var runtime = new BlocksUnitRuntime(registration.Create, registration.Id, host, settings: null, ui: page);

        var unit = new AuthoredUnitHost(
            title,
            tryDraw: _ => false,
            schema,
            values: null,
            log,
            hasBook: registration.IsStrategy,
            apply: values => runtime.ApplySettingsAsync(values),
            clock: () => host.Clock.UtcNow,
            instruments: instruments);

        // The settings are the TERMINAL'S page, beside the unit's own: same presenter, same validation,
        // same Apply — drawn where every unit's settings look alike and no author can get the instrument
        // picker wrong. Only when there is a page at all; a headless unit keeps the WPF expander.
        SettingsPanelView? settings = null;
        if (page is not null && schema.Parameters.Count + unit.Presenter.Actions.Count > 0)
        {
            settings = new SettingsPanelView(unit.Presenter, viewOptions);
            unit.Presenter.SetupDrawnByPage = true;
        }

        // The page replaces the drawing; a unit without one still gets its chrome and says why the
        // middle is empty.
        unit.Presenter.PageContent = page is null
            ? Notice(registration.PageFiles.Count == 0
                ? "This unit has no page. It runs headless; its alerts and log lines appear below."
                : "This unit's page needs the Microsoft Edge WebView2 Runtime, which is not installed.")
            : settings is null ? page : Split(settings, page);

        // Nothing is drawn through the render surface, so there are no frames to pace.
        unit.Freeze();

        var session = new BlocksUnitSession(registration, runtime, unit, page, pageRoot ?? UnitPageFolder.DefaultRoot)
        {
            Settings = settings,
            ModelBook = registration.IsStrategy ? new BlocksModelBook(runtime) : null,
        };
        if (registration.IsStrategy) runtime.PortfolioChanged += session.PushBook;
        return session;
    }

    /// <summary>
    /// Loads the page and starts the unit. Call once the view is in a shown window: WebView2 needs a
    /// window handle to start.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            if (Settings is not null) await Settings.LoadAsync(_pageRoot, _registration.Id, ct);

            if (Page is not null)
                await Page.LoadAsync(UnitPageFolder.Write(_pageRoot, _registration.Id, _registration.PageFiles), ct);

            await Runtime.StartAsync(ct);
            Unit.Presenter.RunState = "Live";
            Unit.Presenter.IsLive = true;
            if (_registration.IsStrategy) PushBook(Runtime.Portfolio);
            // The unit's instruments are known now: tell the books copying it which one it runs on.
            ModelBook?.Refresh();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unit.Presenter.RunState = "Stopped";
            Unit.Presenter.IsLive = false;
            Unit.Presenter.PageContent = Notice($"The unit could not start: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Runtime.PortfolioChanged -= PushBook;
        ModelBook?.Dispose();
        try
        {
            // The unit stops before its page goes, so its last messages never land on a closing browser.
            await Runtime.DisposeAsync();
        }
        finally
        {
            Settings?.Dispose();
            Page?.Dispose();
            Unit.Dispose();
        }
    }

    /// <summary>
    /// What a shell lends a Blocks unit, from its own services.
    ///
    /// <para>The feed hook is the part that matters most. The hub only carries what a broker was asked to
    /// stream, so every subscription the unit makes — whichever instrument, whenever — starts the venue
    /// stream behind it through the same ref-counted ingest the widget windows use.</para>
    /// </summary>
    /// <param name="services">The shell's services.</param>
    /// <param name="log">The activity log.</param>
    /// <param name="instruments">The picker's rows: which broker each id is resolved against.</param>
    /// <param name="offerExport">Hands text to the user (the window's take-away), or null.</param>
    public static BlocksHost HostFor(
        IServiceProvider services,
        InMemoryLogSink log,
        Func<IReadOnlyList<AuthoredUnitInstrument>> instruments,
        Func<string, string, bool>? offerExport = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(instruments);

        T? Get<T>() where T : class => services.GetService(typeof(T)) as T;

        var hub = Get<Core.MarketData.IMarketDataHub>() ?? throw new InvalidOperationException("No market data hub is composed.");
        var clock = Get<Core.Time.IClock>() ?? throw new InvalidOperationException("No clock is composed.");
        var registry = Get<Core.MarketData.IInstrumentRegistry>();
        var ingest = Get<Core.MarketData.IMarketDataIngest>();

        return new BlocksHost(
            hub,
            clock,
            log.Append,
            alert => log.Append(alert.Source, alert.Level.ToString(), alert.Message),
            Store: Get<Core.MarketData.IMarketDataStore>(),
            FindInstrument: registry is null ? null : registry.Get,
            SearchInstruments: registry is null ? null : (text, limit) =>
                registry.All()
                    .Where(i => string.IsNullOrWhiteSpace(text)
                                || i.CanonicalSymbol.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Take(Math.Max(0, limit))
                    .ToList(),
            StateStore: FileUnitStateStore.Default,
            OfferExport: offerExport,
            OpenFeed: ingest is null ? null : (Func<Core.Domain.InstrumentId, MarketFeed, Core.Domain.BarSize, IDisposable?>)((instrument, feed, size) =>
            {
                // Only an id the picker resolved carries the broker its feed starts on. Anything else is
                // fed by whoever else is watching it, or not at all — said by the unit's silence, not by
                // guessing a venue.
                var row = instruments().FirstOrDefault(i => i.Id == instrument);
                if (row is null) return null;

                var requirement = feed switch
                {
                    MarketFeed.Quotes => Core.Strategies.StrategyDataRequirement.L1,
                    MarketFeed.Trades => Core.Strategies.StrategyDataRequirement.TradeTape,
                    MarketFeed.Bars => Core.Strategies.StrategyDataRequirement.Bars,
                    _ => Core.Strategies.StrategyDataRequirement.Depth,
                };

                return AuthoredUnitFeed.Open(ingest, row, requirement, size == default ? Core.Domain.BarSize.OneMinute : size);
            }));
    }

    /// <summary>The book row, summed over every instrument the unit holds.</summary>
    private void PushBook(PortfolioSnapshot snapshot)
    {
        var positions = snapshot.Positions;
        var book = new AuthoredUnitBook(
            positions.Sum(p => p.Units),
            positions.Count == 1 ? positions[0].AveragePrice : 0d,
            snapshot.Account.RealizedPnl,
            snapshot.Account.Equity,
            OpenOrders: 0);

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => Unit.Presenter.Book = book);
        else
            Unit.Presenter.Book = book;
    }

    private static UnitInfo? SafeInfo(BlocksUnitRegistration registration)
    {
        try
        {
            return registration.Create().Info;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The settings panel and the unit's page, side by side, with a grip between them: the
    /// settings are reference material and the picture is the point, so the picture takes the room.</summary>
    private static System.Windows.Controls.Grid Split(SettingsPanelView settings, WebUnitView page)
    {
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(296),
            MinWidth = 180,
            MaxWidth = 520,
        });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var grip = new System.Windows.Controls.GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        System.Windows.Controls.Grid.SetColumn(settings, 0);
        System.Windows.Controls.Grid.SetColumn(grip, 1);
        System.Windows.Controls.Grid.SetColumn(page, 2);

        grid.Children.Add(settings);
        grid.Children.Add(grip);
        grid.Children.Add(page);
        return grid;
    }

    private static System.Windows.Controls.TextBlock Notice(string text) => new()
    {
        Text = text,
        Margin = new Thickness(24),
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        Opacity = 0.8,
    };
}
