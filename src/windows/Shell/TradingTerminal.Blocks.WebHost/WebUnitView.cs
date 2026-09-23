using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TradingTerminal.Blocks.Runtime;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>A script error on a unit's page.</summary>
public sealed record PageError(string Message, string Source, int Line);

/// <summary>Where WebView2 keeps its profile, and how the browser is started.</summary>
/// <param name="UserDataFolder">The WebView2 profile folder. Every view in a process that shares a folder
/// must be created with the same arguments, which is why the probe uses a folder of its own.</param>
/// <param name="AdditionalBrowserArguments">Chromium switches, e.g. to keep rendering while off-screen.</param>
/// <param name="DevTools">Whether F12 opens the developer tools.</param>
/// <param name="ProbeScript">A script of the page probe's own, run before the page's scripts — never set for
/// a window the user sees.</param>
public sealed record WebUnitViewOptions(
    string UserDataFolder, string? AdditionalBrowserArguments = null, bool DevTools = false, string? ProbeScript = null)
{
    /// <summary>The terminal's windows: <c>%LocalAppData%\DaxAlgo Terminal\webview2\units</c>.</summary>
    public static WebUnitViewOptions Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaxAlgo Terminal", "webview2", "units"));
}

/// <summary>
/// A unit's page, shown in WebView2 and connected to its runtime.
///
/// <para><b>The page may only be itself.</b> It is served from a private host name mapped to the unit's
/// own folder. Navigating the frame anywhere else, opening windows, starting downloads and asking for
/// the camera, microphone or location are all refused. A page may still fetch scripts and data over
/// https, which is how a unit uses a charting library.</para>
///
/// <para><b>Posts arrive from a runtime timer thread</b> and are marshalled onto this control's
/// dispatcher. The runtime already coalesces them per topic, so this is at most a few dozen hops a
/// second however fast the unit sends.</para>
/// </summary>
public sealed class WebUnitView : ContentControl, IUnitUiEndpoint, IDisposable
{
    /// <summary>The largest message a page may send, in characters of JSON.</summary>
    public const int MaximumMessageLength = 1_048_576;

    /// <summary>Script errors kept for reporting.</summary>
    public const int MaximumErrors = 50;

    private readonly WebUnitViewOptions _options;
    private readonly WebView2 _web = new();
    private readonly object _gate = new();
    private readonly List<PageError> _errors = [];
    private volatile bool _open;
    private bool _disposed;

    public WebUnitView(WebUnitViewOptions? options = null)
    {
        _options = options ?? WebUnitViewOptions.Default;
        Content = _web;
    }

    /// <summary>True once the page has called <c>dax.ready()</c>, until it navigates or closes.</summary>
    public bool IsOpen => _open;

    public event Action<string, string>? MessageReceived;
    public event Action? Opened;

    /// <summary>Raised on this control's dispatcher for every script error on the page.</summary>
    public event Action<PageError>? ErrorRaised;

    /// <summary>The script errors seen so far, oldest first, bounded.</summary>
    public IReadOnlyList<PageError> Errors
    {
        get { lock (_gate) return [.. _errors]; }
    }

    /// <summary>True when a WebView2 runtime is installed on this machine.</summary>
    public static bool RuntimeAvailable
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Loads <c>index.html</c> from <paramref name="pageFolder"/>. Call on this control's dispatcher.</summary>
    public async Task LoadAsync(string pageFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageFolder);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(_options.UserDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _options.UserDataFolder,
            options: new CoreWebView2EnvironmentOptions(_options.AdditionalBrowserArguments));

        ct.ThrowIfCancellationRequested();
        await _web.EnsureCoreWebView2Async(environment);
        ct.ThrowIfCancellationRequested();

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = _options.DevTools;
        core.Settings.AreDefaultContextMenusEnabled = _options.DevTools;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;

        core.SetVirtualHostNameToFolderMapping(DaxPageScript.HostName, pageFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(DaxPageScript.Source);
        if (_options.ProbeScript is { Length: > 0 } probe) await core.AddScriptToExecuteOnDocumentCreatedAsync(probe);

        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindow;
        core.DownloadStarting += OnDownload;
        core.PermissionRequested += OnPermission;

        core.Navigate(DaxPageScript.EntryUrl);
    }

    public void Post(string topic, string json)
    {
        if (!_open || _disposed) return;

        var message = $"{{\"topic\":{JsonSerializer.Serialize(topic)},\"payload\":{json}}}";
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_open || _disposed || _web.CoreWebView2 is not { } core) return;
            try { core.PostWebMessageAsJson(message); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The page is navigating or the browser is going away; the next open resends state.
            }
        });
    }

    /// <summary>
    /// Runs a script in the page and returns its result as JSON, or null when there is no page. For the
    /// probe's own measurements only — never anything a unit supplied. Call on this control's dispatcher.
    /// </summary>
    internal async Task<string?> EvaluateAsync(string script, CancellationToken ct = default)
    {
        if (_disposed || _web.CoreWebView2 is not { } core) return null;

        try
        {
            var result = await core.ExecuteScriptAsync(script);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // The page is navigating or the browser is going away: nothing to measure.
            return null;
        }
    }

    /// <summary>A PNG of what the page shows right now, or null when there is nothing to capture.</summary>
    public async Task<byte[]?> CaptureAsync(CancellationToken ct = default)
    {
        if (_disposed || _web.CoreWebView2 is not { } core) return null;

        using var stream = new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        ct.ThrowIfCancellationRequested();
        return stream.Length == 0 ? null : stream.ToArray();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; }
        catch (ArgumentException) { return; }

        if (json.Length > MaximumMessageLength)
        {
            Record(new PageError($"The page sent a message of {json.Length:N0} characters; the limit is {MaximumMessageLength:N0}.", string.Empty, 0));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("dax", out _)) return;

            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "ready":
                    _open = true;
                    Opened?.Invoke();
                    break;

                case "message" when root.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.String:
                    var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : "null";
                    MessageReceived?.Invoke(topic.GetString()!, payload);
                    break;

                case "error":
                    Record(new PageError(
                        root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("source", out var source) ? source.GetString() ?? string.Empty : string.Empty,
                        root.TryGetProperty("line", out var line) && line.TryGetInt32(out var n) ? n : 0));
                    break;
            }
        }
        catch (JsonException)
        {
            // Not the bridge's shape; ignored.
        }
    }

    private void Record(PageError error)
    {
        lock (_gate)
        {
            if (_errors.Count >= MaximumErrors) _errors.RemoveAt(0);
            _errors.Add(error);
        }

        ErrorRaised?.Invoke(error);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, DaxPageScript.HostName, StringComparison.OrdinalIgnoreCase))
        {
            // A reload of the unit's own page starts it over: it is not open until it says ready again.
            _open = false;
            return;
        }

        e.Cancel = true;
        Record(new PageError($"Navigation to '{e.Uri}' was blocked; a unit's page stays on its own page.", string.Empty, 0));
    }

    private static void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private static void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

    private static void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = CoreWebView2PermissionState.Deny;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _open = false;

        if (_web.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= OnWebMessage;
            core.NavigationStarting -= OnNavigationStarting;
            core.NewWindowRequested -= OnNewWindow;
            core.DownloadStarting -= OnDownload;
            core.PermissionRequested -= OnPermission;
        }

        _web.Dispose();
    }
}
