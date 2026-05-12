using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TradingTerminal.App.Composition;
using TradingTerminal.App.Logging;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Notifications;
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

        var inMemoryLogSink = new InMemoryLogSink();
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(assemblyDir)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.SetBasePath(assemblyDir);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                cfg.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

                // Per-user override file edited by the Settings tab. Layered last so the
                // UI's writes win over what's shipped in appsettings.json.
                cfg.AddJsonFile(NotificationsUserFile.Path, optional: true, reloadOnChange: true);
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
                // Options
                services.Configure<InteractiveBrokersOptions>(
                    ctx.Configuration.GetSection(InteractiveBrokersOptions.SectionName));
                services.Configure<NinjaTraderOptions>(
                    ctx.Configuration.GetSection(NinjaTraderOptions.SectionName));
                services.Configure<CTraderOptions>(
                    ctx.Configuration.GetSection(CTraderOptions.SectionName));

                // Cross-cutting infrastructure
                services.AddSingleton(inMemoryLogSink);
                services.AddTradingTerminalInfrastructure();
                services.AddNotifications(ctx.Configuration);

                // Feature modules — each is a one-line manifest entry.
                services.AddStrategyPlugins();
                services.AddBrokerLoginForms();
                services.AddShell();
                services.AddBacktestSurface();
                services.AddSettingsSurface();
            })
            .Build();

        await _host.StartAsync();

        // Hold the app open across the login → main-window transition.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

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

        var mainFactory = _host!.Services.GetRequiredService<IMainShellFactory>();
        var mainWindow = mainFactory.Create();
        MainWindow = mainWindow;
        mainWindow.Show();
        loginWindow.Close();
    }

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
