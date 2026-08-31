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
using TradingTerminal.App.Security;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure;
using TradingTerminal.Login;
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
        var processMitigations = ProcessMitigations.ApplyEarly();
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, "--mitigation-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(processMitigations.SmokePassed ? 0 : 2);
            return;
        }
        if (!processMitigations.SmokePassed)
            throw new InvalidOperationException(processMitigations.Failure);

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        // Dev/QA escape hatch: `--bypass-login` (alias `--no-login`) skips the broker sign-in
        // surface and opens the shell directly.
        var bypassLoginRequested = e.Args.Any(a =>
            string.Equals(a, "--bypass-login", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--no-login", StringComparison.OrdinalIgnoreCase));

        // Seed the shared strategy-pill converters into Application resources before any window is
        // shown, so {StaticResource StrategyTagsConverter} / {StaticResource StrategyClassConverter}
        // resolve in the MainWindow strategy list. Mirrors InstrumentPicker's ctor-time registration
        // (MC3074 same-assembly XAML workaround).
        StrategyDataRequirementConverter.EnsureConverterRegistered();
        StrategyClassificationConverter.EnsureConverterRegistered();
        UnsignedStrategyConverter.EnsureConverterRegistered();

        // The Activity Log sink is WPF-free; point its UI-thread
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
        // File-picker seam (WPF-free in UI.Core) — point it at the
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
        // Typed-confirmation prompt (UiPrompt seam). Uses MahApps' own input dialog so the gate
        // looks like the rest of the shell; an unwired host returns null, i.e. refuses.
        TradingTerminal.UI.UiPrompt.AskForText = (title, message) =>
        {
            var dialog = new TradingTerminal.UI.Controls.TextPromptDialog(title, message)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            return dialog.ShowDialog() == true ? dialog.EnteredText : null;
        };
        var inMemoryLogSink = new InMemoryLogSink();

        // Last-line crash nets (shared implementation in TradingTerminal.UI): a broken window
        // callback must not hard-kill every live feed, and a distributed build must leave a
        // crash report behind. Wired before the host builds so even composition failures report.
        TradingTerminal.UI.CrashGuard.Install("DaxAlgo Terminal Basic", inMemoryLogSink.Append);
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(assemblyDir)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.SetBasePath(assemblyDir);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                // Per-environment dev overrides (selected via DOTNET_ENVIRONMENT from the launch
                // profiles). Layered over appsettings.json but under
                // appsettings.local.json so a developer's local file still wins.
                cfg.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                    optional: true, reloadOnChange: true);
                cfg.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

                // Per-user override files edited by the Settings tabs. Layered last so the
                // UI's writes win over what's shipped in appsettings.json.
                cfg.AddJsonFile(NotificationsUserFile.Path, optional: true, reloadOnChange: true);
                cfg.AddJsonFile(TradingTerminal.App.Archive.ArchiveUserFile.Path, optional: true, reloadOnChange: true);
                cfg.AddJsonFile(TradingTerminal.App.Authoring.AiCodegenUserFile.Path, optional: true, reloadOnChange: true);
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

                // Dev-only switches. Off in the shipped appsettings and in this edition's single
                // launch profile; the installers' New User / Testing profiles turn parts of it on via
                // their appsettings.{Env}.json overlay.
                services.Configure<DevOptions>(
                    ctx.Configuration.GetSection(DevOptions.SectionName));

                // Cross-cutting: the shared Activity Log sink instance (same one the Serilog sink above
                // writes to). Registered before AddCoreShell so its TryAdd is a no-op and this instance wins.
                services.AddSingleton(inMemoryLogSink);

                // This exe IS the Basic edition — the edition is fixed per shell project, not
                // configuration. (AppEdition is a value type, so register it through the non-generic
                // Type/instance overload.)
                services.AddSingleton(typeof(AppEdition), AppEdition.Basic);

                // Broker layer: shared broker-neutral infrastructure + every broker. Keep the
                // credentialed forms paired with their brokers because the login factory resolves
                // every registered form while constructing the sign-in window.
                services.AddInfrastructureCore();
                services.AddKeylessBrokers();
                services.AddCredentialedBrokers();
                services.AddCredentialedLoginForms();

                // The core composition (pipeline / archive / notifications / strategy plug-ins /
                // login / shell + window host / support / settings + cross-cutting singletons).
                // No Pro surface is registered —
                // this edition's exe does not even reference those projects.
                services.AddCoreShell(ctx.Configuration);
            })
            .Build();

        await _host.StartAsync();

        // Runtime plugin-fault watchdog: unhandled dispatcher/task faults whose stack lies in a
        // plugin's load context are attributed to that plugin (one Activity Log warning per strike);
        // repeated faults quarantine it for the NEXT start — CrashGuard still owns keeping this
        // session alive. Persisted state is absent only in odd host setups; then the net is off.
        var pluginHost = _host.Services.GetRequiredService<Infrastructure.Plugins.PluginHostContext>();

        // Authored units back into the catalog. The loader gated and loaded them during composition;
        // turning an assembly into a card needs the registries, which are singletons and so only exist
        // now. Without this pass an installed unit is loaded and invisible — the worst of both, since it
        // cost the load and shows nothing for it.
        var boundUnits = TradingTerminal.UI.Strategies.PluginUnitBinder.Bind(
            pluginHost.LoadedPlugins
                .Where(p => p.Image is not null)
                .Select(p =>
                {
                    // Read once: the manifest carries both halves of the unit's identity, and taking
                    // only the id is what left an installed strategy titled after its type name.
                    var manifest = Infrastructure.Plugins.PluginManifest.TryRead(
                        System.IO.Path.GetDirectoryName(p.AssemblyPath)!);

                    return (
                        PluginId: manifest?.Id
                            ?? System.IO.Path.GetFileNameWithoutExtension(p.AssemblyPath),
                        DisplayName: manifest?.Name,
                        Image: p.Image!);
                }),
            _host.Services.GetService<TradingTerminal.UI.Strategies.IStrategyKernelRegistry>(),
            _host.Services.GetService<TradingTerminal.UI.Strategies.IVisualizerRegistry>());

        if (boundUnits.Total > 0 || boundUnits.Skipped.Count > 0)
            inMemoryLogSink.Append("Plugins", "Information", boundUnits.ToString());

        foreach (var skipped in boundUnits.Skipped)
            inMemoryLogSink.Append("Plugins", "Warning", $"Authored unit skipped — {skipped}");

        if (pluginHost.State is { } pluginFaultState)
            TradingTerminal.UI.Diagnostics.PluginFaultWatchdog.Attach(this, strikeLimit: 3,
                onStrikeOut: (plugin, reason) =>
                {
                    pluginFaultState.Quarantine(plugin, reason);
                    inMemoryLogSink.Append("Plugins", "Warning",
                        $"Strategy plugin '{plugin}' quarantined after repeated faults — it will not load on the next start (re-enable in Extensions). {reason}");
                },
                log: (source, level, message) => inMemoryLogSink.Append(source, level, message));

        // Point every strategy instrument picker at the canonical registry instead
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

        // Automated smoke sweep (dev/CI only): `--smoke-strategies` skips login, opens every
        // catalog strategy window once through the real IStrategyFactory path — the cross-ALC
        // plugin windows included — writes a PASS/FAIL report next to the exe, and exits with a
        // non-zero code on any failure. See TradingTerminal.UI.Diagnostics.StrategyWindowSmoke.
        if (e.Args.Any(a => string.Equals(a, "--smoke-strategies", StringComparison.OrdinalIgnoreCase)))
        {
            ShowMain();
            var plugins = _host.Services.GetRequiredService<Infrastructure.Plugins.PluginHostContext>();
            var exitCode = await TradingTerminal.UI.Diagnostics.StrategyWindowSmoke.RunAsync(
                _host.Services.GetRequiredService<Core.Strategies.IStrategyFactory>(),
                Path.Combine(AppContext.BaseDirectory, "smoke-strategies.txt"),
                plugins.LoadedPlugins.Select(p => p.Name));
            Shutdown(exitCode);
            return;
        }

        // Dev launch profiles or the full `--bypass-login` flag skip the broker window.
        var dev = _host.Services.GetRequiredService<IOptions<DevOptions>>().Value;
        if (dev.BypassLogin || bypassLoginRequested)
        {
            // A command-line bypass has no broker list of its own, and there is no in-process feed
            // to fall back on since the Simulated broker was removed. The shell opens with no data;
            // the user connects a broker from the menu.
            await ConnectAndShowMainAsync(dev);
        }
        else
        {
            ShowLoginAndProceed();
        }
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

    protected override void OnExit(ExitEventArgs e)
    {
        var host = _host;
        _host = null;
        try
        {
            if (host is not null)
            {
                try
                {
                    host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "The application host did not stop cleanly within the shutdown window.");
                }
                finally
                {
                    try { host.Dispose(); }
                    catch (Exception ex) { Log.Warning(ex, "The application host could not be disposed cleanly."); }
                }
            }
        }
        finally
        {
            try { Log.CloseAndFlush(); }
            finally { base.OnExit(e); }
        }
    }
}
