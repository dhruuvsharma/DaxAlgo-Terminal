using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Login;

namespace TradingTerminal.App.Shell;

internal sealed class LoginShellFactory : ILoginShellFactory
{
    private readonly IServiceProvider _services;

    public LoginShellFactory(IServiceProvider services) => _services = services;

    public Window Create(EventHandler<bool> onCompleted)
    {
        var window = _services.GetRequiredService<LoginWindow>();
        var vm = _services.GetRequiredService<LoginViewModel>();
        vm.LoginCompleted += onCompleted;
        window.DataContext = vm;
        return window;
    }
}
