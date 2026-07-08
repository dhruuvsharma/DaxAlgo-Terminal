using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using TradingTerminal.App.Composition;
using TradingTerminal.App.Logging;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure;
using TradingTerminal.UI.Converters;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App;

public partial class App : Application
{
    private IHost? _host;

    public IServiceProvider Services => _host!.Services;

    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Seed the shared strategy-pill converters into Application resources before any window is
        // shown, so {StaticResource StrategyTagsConverter} / {StaticResource StrategyClassConverter}
        // resolve in the MainWindow strategy list. Mirrors InstrumentPicker's ctor-time registration
        // (MC3074 same-assembly XAML workaround).
        StrategyDataRequirementConverter.EnsureConverterRegistered();
        StrategyClassificationConverter.EnsureConverterRegistered();

        // The Activity Log sink is now WPF-free (shared with the Avalonia head); point its UI-thread
        // marshaller at the WPF Dispatcher so background-thread appends (Serilog, strategies) are safe.
        InMemoryLogSink.UiPost = action =>
        {
            var dispatcher = Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        };
        // Same for the VM marshaling helper (UiThread) now that it's WPF-free in UI.Core.
        TradingTerminal.UI.UiThread.Marshal = action =>
        {
            var d = Current?.Dispatcher;
            if (d is null || d.CheckAccess()) return action();
            return d.InvokeAsync(action).Task.Unwrap();
        };
        // File-picker seam (WPF-free in UI.Core; shared with the Avalonia head) — point it at the
        // WPF dialogs so tool VMs that load/save files keep working on the WPF shell.
        TradingTerminal.UI.UiFile.OpenAsync = (desc, exts) =>
        {
            var filter = $"{desc}|{string.Join(";", exts.Select(e => "*." + e))}|All files (*.*)|*.*";
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileName : (string?)null);
        };
        TradingTerminal.UI.UiFile.SaveAsync = (desc, exts, name) =>
        {
            var filter = $"{desc}|{string.Join(";", exts.Select(e => "*." + e))}";
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = name };
            return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileName : (string?)null);
        };
        var inMemoryLogSink = new InMemoryLogSink();
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(assemblyDir)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.SetBasePath(assemblyDir);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // Per-environment dev overrides (selected via DOTNET_ENVIRONMENT from the launch
                // profiles: DevSim / DevReplay). Layered over appsettings.json but under
                // appsettings.local.json so a developer's local file still wins.
                cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                    optional: true, reloadOnChange: true);
                cfg.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

                // Per-user override files edited by the Settings tabs. Layered last so the
                // UI's writes win over what's shipped in appsettings.json. (No Research user file —
                // the Paper Lab / sidecar surface is Professional-only.)
                cfg.AddJsonFile(NotificationsUserFile.Path, optional: true, reloadOnChange: true);
                cfg.AddJsonFile(TradingTerminal.App.Archive.ArchiveUserFile.Path, optional: true, reloadOnChange: true);
            })
            .UseSerilog((ctx, services, lc) =>
            {
                lc.ReadFrom.Configuration(ctx.Configuration);

                var minLevel = ctx.Configuration["Logging:MinimumLevel"] ?? "Information";
                lc.MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(minLevel, out var lv)
                    ? lv : Serilog.Events.LogEventLevel.Information);

                var filePath = ctx.Configuration["Logging:FilePath"] ?? "logs/terminal-.log";
                lc.WriteTo.File(filePath, rollingInterval: RollingInterval.Day);
                lc.WriteTo.Debug();
                lc.WriteTo.Sink(new ObservableCollectionLogSink(inMemoryLogSink));
            })
            .ConfigureServices((ctx, services) =>
            {
                // Options — keyless brokers only (the credentialed brokers are not registered in
                // this edition, so their options are never read).
                services.Configure<BinanceOptions>(
                    ctx.Configuration.GetSection(BinanceOptions.SectionName));
                services.Configure<CoinbaseOptions>(
                    ctx.Configuration.GetSection(CoinbaseOptions.SectionName));
                services.Configure<BybitOptions>(
                    ctx.Configuration.GetSection(BybitOptions.SectionName));
                services.Configure<KrakenOptions>(
                    ctx.Configuration.GetSection(KrakenOptions.SectionName));
                services.Configure<OkxOptions>(
                    ctx.Configuration.GetSection(OkxOptions.SectionName));

                // Dev-only switches + the Simulated broker feed (off in the shipped appsettings;
                // turned on by the DevSim/DevReplay environment files).
                services.Configure<DevOptions>(
                    ctx.Configuration.GetSection(DevOptions.SectionName));
                services.Configure<SimulatedBrokerOptions>(
                    ctx.Configuration.GetSection(SimulatedBrokerOptions.SectionName));

                // Cross-cutting: the shared Activity Log sink instance (same one the Serilog sink above
                // writes to). Registered before AddCoreShell so its TryAdd is a no-op and this instance wins.
                services.AddSingleton(inMemoryLogSink);

                // This exe IS the Basic edition — the edition is fixed per shell project, not
                // configuration. (AppEdition is a value type, so register it through the non-generic
                // Type/instance overload.)
                services.AddSingleton(typeof(AppEdition), AppEdition.Basic);

                // Broker layer: shared broker-neutral infrastructure + the keyless brokers only
                // (public crypto feeds + Simulated). The credentialed set (IB/NT/cTrader/Alpaca/
                // Ironbeam/LSE/Upstox) is an Intermediate-and-up surface and is NOT registered here,
                // which also keeps their login tiles off the sign-in screen.
                services.AddInfrastructureCore();
                services.AddKeylessBrokers();

                // The core composition (pipeline / archive / notifications / regime / strategy plug-ins /
                // login / shell + window host / support / settings / AI-analyst seam + the common tool &
                // chart surfaces + cross-cutting singletons). No Professional surface is registered —
                // this edition's exe does not even reference those projects.
                services.AddCoreShell(ctx.Configuration);
            })
            .Build();

        await _host.StartAsync();

        // Point every instrument picker (strategies, tools, charts) at the canonical registry instead
        // of the hardcoded fallback. The registry is loaded by the pipeline at startup and keeps
        // filling as brokers connect, so all dropdowns show the real discovered universe. Mirrors the
        // UiThread.Marshal / InMemoryLogSink.UiPost startup hooks above.
        var registry = _host.Services.GetRequiredService<Core.MarketData.IInstrumentRegistry>();
        TradingTerminal.UI.SignalInstrumentCatalog.Source = () =>
            TradingTerminal.UI.SignalInstrumentCatalog.FromRegistry(registry);

        // Apply the persisted theme before any window is shown, so the login window already wears it.
        _host.Services.GetRequiredService<TradingTerminal.UI.Theming.IThemeManager>().ApplySaved();

        // Theme every MetroWindow's OS-level title bar from the active palette. MahApps otherwise pins
        // the title bar to its base accent (a fixed blue on every theme, which clashes badly with the
        // light Greek palette). One class handler covers the whole app — including subclassed and
        // plugin-provided windows — with no per-window XAML. SetResourceReference keeps the brushes
        // DynamicResource-equivalent, so a live theme swap re-skins the title bars too.
        EventManager.RegisterClassHandler(typeof(MahApps.Metro.Controls.MetroWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(static (sender, _) =>
            {
                if (sender is not MahApps.Metro.Controls.MetroWindow w) return;
                w.SetResourceReference(MahApps.Metro.Controls.MetroWindow.WindowTitleBrushProperty, "Gradient.Chrome");
                w.SetResourceReference(MahApps.Metro.Controls.MetroWindow.NonActiveWindowTitleBrushProperty, "Background.Surface");
                w.SetResourceReference(MahApps.Metro.Controls.MetroWindow.TitleForegroundProperty, "Text.Primary");
                w.SetResourceReference(MahApps.Metro.Controls.MetroWindow.GlowBrushProperty, "Accent.Brush");
            }));

        // Hold the app open across the login → main-window transition.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // Dev launch profiles can skip the login window entirely (see DevOptions).
        var dev = _host.Services.GetRequiredService<IOptions<DevOptions>>().Value;
        if (dev.BypassLogin)
            await ConnectAndShowMainAsync(dev);
        else
            ShowLoginAndProceed();
    }

    private void ShowLoginAndProceed()
    {
        var loginFactory = _host!.Services.GetRequiredService<ILoginShellFactory>();
        Window? loginWindow = null;
        loginWindow = loginFactory.Create((_, success) => OnLoginCompleted(loginWindow!, success));
        MainWindow = loginWindow;
        loginWindow.Show();
    }

    private void OnLoginCompleted(Window loginWindow, bool success)
    {
        if (!success)
        {
            Shutdown();
            return;
        }

        var mainWindow = ShowMain();
        loginWindow.Close();
        RunSupportPrompt(mainWindow);
    }

    /// <summary>
    /// Dev login-bypass path: auto-connect the configured brokers (same call the login forms make,
    /// non-blocking — connection state flows reactively into the shell) and open the main window.
    /// A broker that's unavailable or fails to connect is logged to the Activity Log, never fatal.
    /// </summary>
    private async Task ConnectAndShowMainAsync(DevOptions dev)
    {
        var selector = _host!.Services.GetRequiredService<IBrokerSelector>();
        var log = _host.Services.GetRequiredService<InMemoryLogSink>();

        foreach (var kind in dev.AutoConnectBrokers)
        {
            if (!selector.IsAvailable(kind))
            {
                log.Append("Dev", "Warning", $"Auto-connect skipped — broker {kind} is not available in this build.");
                continue;
            }

            try
            {
                log.Append("Dev", "Information", $"Login bypassed — auto-connecting {kind}…");
                await selector.ConnectAsync(kind);
            }
            catch (Exception ex)
            {
                log.Append("Dev", "Error", $"Auto-connect {kind} failed: {ex.Message}");
            }
        }

        RunSupportPrompt(ShowMain());
    }

    private Window ShowMain()
    {
        var mainFactory = _host!.Services.GetRequiredService<IMainShellFactory>();
        var mainWindow = mainFactory.Create();
        MainWindow = mainWindow;
        mainWindow.Show();
        return mainWindow;
    }

    /// <summary>Friendly once-per-launch "support the developer" nudge, after a short randomised delay.</summary>
    private void RunSupportPrompt(Window owner) =>
        _host!.Services.GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>()
            .MaybeShowOnLaunch(owner);

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
