using System.Windows;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Factory Method seam for the WPF shell windows. The application bootstrap (<c>App.xaml.cs</c>)
/// resolves these from DI and never references concrete <c>LoginWindow</c> / <c>MainWindow</c>
/// or their view-models — every window leaves the factory with its <c>DataContext</c> already wired.
///
/// One factory per shell window keeps the bootstrap testable and lets us swap out the actual
/// window/VM pair (for instance, a different login UX for an embedded build) without touching
/// the application lifecycle code.
/// </summary>
public interface ILoginShellFactory
{
    /// <summary>Builds the login window with its view-model wired in.</summary>
    /// <param name="onCompleted">Invoked with <c>true</c> on a successful sign-in, <c>false</c> if the user cancelled.</param>
    Window Create(EventHandler<bool> onCompleted);
}

public interface IMainShellFactory
{
    /// <summary>Builds the main shell window with its view-model wired in. Caller shows it.</summary>
    Window Create();
}
