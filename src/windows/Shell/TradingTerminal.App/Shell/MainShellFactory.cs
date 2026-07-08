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
        // Hand the shell VM the edition's menu UserControl (registered as IShellMenuBar). Null in a
        // build that ships no menu — the ContentControl region simply stays empty.
        vm.MenuBar = _services.GetService<IShellMenuBar>();
        window.DataContext = vm;
        return window;
    }
}
