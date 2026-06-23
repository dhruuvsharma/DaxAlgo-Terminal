using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.Composition;
using TradingTerminal.App.Avalonia.ViewModels;
using TradingTerminal.App.Avalonia.Views;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia;

public partial class App : Application
{
    /// <summary>The composed DI graph; views resolve ported per-strategy VMs from here.</summary>
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Point the shared (WPF-free) UI-thread marshallers at Avalonia's dispatcher — the same
        // hooks the WPF shell sets to its Dispatcher. This is what lets the portable view-models
        // and the universal Activity Log run unchanged on Avalonia.
        InMemoryLogSink.UiPost = action => Dispatcher.UIThread.Post(action);
        UiThread.Marshal = MarshalToUiThread;

        // Compose the headless DI graph and resolve the root VM from it (mirrors the WPF App).
        Services = ServiceConfiguration.Build();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Runs the work on Avalonia's UI thread and surfaces its completion/exception back to the caller.
    private static Task MarshalToUiThread(Func<Task> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) return work();

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await work().ConfigureAwait(true); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
