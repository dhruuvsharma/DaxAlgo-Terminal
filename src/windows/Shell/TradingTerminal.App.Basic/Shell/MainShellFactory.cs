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
        var vm = _services.GetRequiredService<MainWindowViewModel>();
        window.DataContext = vm;
        return window;
    }
}
