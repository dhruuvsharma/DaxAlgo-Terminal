using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TradingTerminal.App.Logging;
using TradingTerminal.App.Login;
using TradingTerminal.App.Strategies;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure;
using TradingTerminal.Strategies.Rsi;
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
                services.Configure<InteractiveBrokersOptions>(
                    ctx.Configuration.GetSection(InteractiveBrokersOptions.SectionName));

                services.AddSingleton(inMemoryLogSink);

                services.AddTradingTerminalInfrastructure();

                services.AddSingleton<IStrategyFactory, StrategyFactory>();

                // Plug-in registrations. Add a new strategy by adding one line here.
                services.AddRsiStrategy();

                // Login flow.
                services.AddSingleton<CredentialStore>();
                services.AddTransient<LoginViewModel>();
                services.AddTransient<LoginWindow>();

                services.AddSingleton<MainWindowViewModel>();
                services.AddTransient<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // Hold the app open across the login → main-window transition (we briefly have no MainWindow
        // assigned; OnLastWindowClose still terminates the app correctly when MainWindow ultimately closes).
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        ShowLoginAndProceed();
    }

    private void ShowLoginAndProceed()
    {
        var loginWindow = _host!.Services.GetRequiredService<LoginWindow>();
        var loginVm = _host.Services.GetRequiredService<LoginViewModel>();
        loginWindow.DataContext = loginVm;

        loginVm.LoginCompleted += OnLoginCompleted;
        MainWindow = loginWindow;   // bridges window ownership during the login phase
        loginWindow.Show();

        void OnLoginCompleted(object? sender, bool success)
        {
            loginVm.LoginCompleted -= OnLoginCompleted;

            if (!success)
            {
                Shutdown();
                return;
            }

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _host.Services.GetRequiredService<MainWindowViewModel>();
            MainWindow = mainWindow;
            mainWindow.Show();
            loginWindow.Close();
        }
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
