using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.Composition;
using TradingTerminal.App.Avalonia.Login;
using TradingTerminal.App.Avalonia.Shell;
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
        WireFilePicker();

        // Compose the headless DI graph and resolve the root VM from it (mirrors the WPF App).
        Services = ServiceConfiguration.Build();

        // Point every instrument picker at the canonical registry instead of the hardcoded fallback
        // (mirrors the WPF shell). The registry fills at startup + as brokers connect.
        var registry = Services.GetRequiredService<TradingTerminal.Core.MarketData.IInstrumentRegistry>();
        TradingTerminal.UI.SignalInstrumentCatalog.Source = () =>
            TradingTerminal.UI.SignalInstrumentCatalog.FromRegistry(registry);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show the login screen first; hand off to the main shell once a broker connects
            // (mirrors the WPF ILoginShellFactory/IMainShellFactory handoff).
            var loginVm = Services.GetRequiredService<LoginViewModel>();
            var login = new LoginWindow { DataContext = loginVm };
            loginVm.Connected += _ => Dispatcher.UIThread.Post(() =>
            {
                var main = new MainWindow { DataContext = Services!.GetRequiredService<MainWindowViewModel>() };
                desktop.MainWindow = main;
                main.Show();
                login.Close();
            });
            desktop.MainWindow = login;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Points the portable UiFile seam at Avalonia's StorageProvider (the cross-platform file picker),
    // so tool VMs that load/save files work on the Avalonia head as they do on WPF.
    private static void WireFilePicker()
    {
        UiFile.OpenAsync = async (desc, exts) =>
        {
            if (ActiveTopLevel()?.StorageProvider is not { } sp) return null;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(desc) { Patterns = exts.Select(e => "*." + e).ToArray() } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };
        UiFile.SaveAsync = async (desc, exts, name) =>
        {
            if (ActiveTopLevel()?.StorageProvider is not { } sp) return null;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = name,
                FileTypeChoices = new[] { new FilePickerFileType(desc) { Patterns = exts.Select(e => "*." + e).ToArray() } },
            });
            return file?.TryGetLocalPath();
        };
    }

    /// <summary>The active (or main) window to parent file dialogs to.</summary>
    private static TopLevel? ActiveTopLevel()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        return null;
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
