using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.App.Shell;

internal sealed class MainShellFactory : IMainShellFactory
{
    private readonly IServiceProvider _services;

    public MainShellFactory(IServiceProvider services) => _services = services;

    public Window Create()
    {
        var window = _services.GetRequiredService<MainWindow>();
        window.DataContext = _services.GetRequiredService<MainWindowViewModel>();
        return window;
    }
}
