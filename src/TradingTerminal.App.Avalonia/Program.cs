using Avalonia;

namespace TradingTerminal.App.Avalonia;

internal static class Program
{
    // Avalonia desktop entry point. Cross-platform: Windows, Linux (X11), and Raspberry Pi.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
