using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.UI;

/// <summary>
/// Last-line crash handling for the WPF shells. Without this, any unhandled exception on the
/// dispatcher hard-kills the process with no trace for the user — unacceptable for a distributed
/// build. <see cref="Install"/> wires three nets:
///
/// <list type="bullet">
/// <item><b>DispatcherUnhandledException</b> — writes a crash report, logs to the Activity Log,
/// shows a friendly dialog, and marks the exception handled so one broken window/callback doesn't
/// take down every live feed. Repeated failures within a few seconds stop showing dialogs (loop
/// protection) but keep being reported.</item>
/// <item><b>TaskScheduler.UnobservedTaskException</b> — reported and marked observed; these are
/// background faults that would otherwise surface only at GC time.</item>
/// <item><b>AppDomain.UnhandledException</b> — non-recoverable (the CLR is tearing down); the
/// report is written so the next session has something to diagnose.</item>
/// </list>
///
/// Reports land under <c>%LocalAppData%\DaxAlgo Terminal\crash-reports\</c>, newest kept to a
/// small cap. Lives in TradingTerminal.UI so all three edition shells share one implementation —
/// each shell only calls <see cref="Install"/> once from OnStartup.
/// </summary>
public static class CrashGuard
{
    private const int MaxReportsKept = 30;

    private static readonly object Gate = new();
    private static DateTime _lastDialogUtc = DateTime.MinValue;
    private static string _appName = "DaxAlgo Terminal";
    private static Action<string, string, string>? _log;

    /// <summary>Directory the crash reports are written to.</summary>
    public static string ReportDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "crash-reports");

    /// <summary>Wire the handlers. Call once, early in OnStartup. <paramref name="log"/> is an
    /// optional bridge into the shared Activity Log (source, level, message).</summary>
    public static void Install(string appName, Action<string, string, string>? log = null)
    {
        _appName = appName;
        _log = log;

        if (Application.Current is { } app)
            app.DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteReport("dispatcher", e.Exception);
        _log?.Invoke("System", "ERROR",
            $"Unhandled UI exception: {e.Exception.GetType().Name}: {e.Exception.Message} (report: {path ?? "n/a"})");

        // Keep the terminal alive — data/signals only, so continuing is safe; the alternative is
        // killing every live feed because one window's callback threw.
        e.Handled = true;

        // Loop protection: a render/binding fault that rethrows every frame must not stack dialogs.
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastDialogUtc < TimeSpan.FromSeconds(10)) return;
            _lastDialogUtc = now;
        }

        var message =
            $"Something went wrong in {_appName}:\n\n" +
            $"{e.Exception.GetType().Name}: {Truncate(e.Exception.Message, 300)}\n\n" +
            (path is not null
                ? $"A crash report was saved to:\n{path}\n\n"
                : "") +
            "The terminal keeps running, but if things look wrong, save your work and restart.";
        try
        {
            MessageBox.Show(message, $"{_appName} — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // Shutting down / no UI — the report on disk is the record.
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var path = WriteReport("task", e.Exception);
        _log?.Invoke("System", "WARN",
            $"Unobserved task exception: {e.Exception.GetBaseException().GetType().Name}: " +
            $"{e.Exception.GetBaseException().Message} (report: {path ?? "n/a"})");
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        // Process is dying (IsTerminating is almost always true here) — just leave the evidence.
        WriteReport("fatal", e.ExceptionObject as Exception);
    }

    private static string? WriteReport(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);
            var path = Path.Combine(ReportDirectory,
                $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{kind}.txt");
            File.WriteAllText(path,
                $"{_appName}\n" +
                $"When (UTC): {DateTime.UtcNow:O}\n" +
                $"Kind:       {kind}\n" +
                $"OS:         {Environment.OSVersion} · .NET {Environment.Version}\n" +
                $"\n{ex?.ToString() ?? "(no exception object)"}\n");
            TrimOldReports();
            return path;
        }
        catch
        {
            return null;   // never let the crash handler crash
        }
    }

    private static void TrimOldReports()
    {
        var files = new DirectoryInfo(ReportDirectory)
            .GetFiles("crash-*.txt")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxReportsKept);
        foreach (var f in files)
        {
            try { f.Delete(); }
            catch { /* locked/permissions — ignore */ }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
