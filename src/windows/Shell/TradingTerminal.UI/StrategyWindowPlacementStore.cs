using System.IO;
using System.Text.Json;
using System.Windows;

namespace TradingTerminal.UI;

/// <summary>One strategy window's remembered placement: the normal (restore) bounds plus whether it
/// was maximized. Persisted per strategy id so each window reopens where the user last left it.</summary>
public sealed record StrategyWindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Best-effort, file-backed store for per-strategy window placement, keyed by
/// <see cref="LiveSignalStrategyViewModelBase.StrategyId"/>. Used by <see cref="StrategyWindowBase"/>
/// to open a strategy maximized the first time and restore its last size/position/state thereafter.
///
/// <para>Persistence is intentionally best-effort: any IO/JSON failure is swallowed so a corrupt or
/// unwritable file can never stop a strategy window from opening. The cache is loaded once and written
/// back on each save. Stored under the same <c>%LOCALAPPDATA%/DaxAlgo Terminal</c> root the rest of the
/// app uses for user files.</para>
/// </summary>
public static class StrategyWindowPlacementStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "strategy-windows.json");

    private static readonly object Gate = new();
    private static Dictionary<string, StrategyWindowPlacement>? _cache;

    /// <summary>Returns the remembered placement for <paramref name="strategyId"/>, or null if the
    /// strategy has never been positioned (first open) or the store can't be read.</summary>
    public static StrategyWindowPlacement? Load(string strategyId)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _cache!.TryGetValue(strategyId, out var p) ? p : null;
        }
    }

    /// <summary>Records the latest placement for <paramref name="strategyId"/> and flushes the store.
    /// Swallows any failure — losing a remembered size must never surface as an error.</summary>
    public static void Save(string strategyId, StrategyWindowPlacement placement)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _cache![strategyId] = placement;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache));
            }
            catch
            {
                // best-effort: an unwritable profile dir must not break window close.
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="window"/> open full-size and remember where the user leaves it, keyed by
    /// <paramref name="key"/> (the strategy id). The first time a strategy is opened it starts maximized;
    /// thereafter it restores the remembered size, position and maximized state. A saved rectangle that no
    /// longer intersects any monitor (e.g. an unplugged second screen) falls back to maximized so the
    /// window can't open off-screen. Call this once, before <see cref="Window.Show"/>; it's base-class
    /// agnostic, so it covers both <see cref="StrategyWindowBase"/> and plain MetroWindow strategies.
    /// </summary>
    public static void Attach(Window window, string key)
    {
        window.SourceInitialized += (_, _) =>
        {
            var saved = Load(key);
            if (saved is not null && IsOnScreen(saved))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = saved.Left;
                window.Top = saved.Top;
                window.Width = saved.Width;
                window.Height = saved.Height;
                window.WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
            }
            else
            {
                // First open (or off-screen saved bounds): fill the screen.
                window.WindowState = WindowState.Maximized;
            }
        };

        window.Closed += (_, _) =>
        {
            // RestoreBounds gives the normal (non-maximized) rectangle even when maximized, so we
            // remember the underlying size rather than the full-screen one.
            var bounds = window.RestoreBounds;
            if (bounds.IsEmpty || bounds.Width < 200 || bounds.Height < 150) return;
            Save(key, new StrategyWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized));
        };
    }

    /// <summary>True when the saved rectangle overlaps the virtual desktop enough to grab and move.</summary>
    private static bool IsOnScreen(StrategyWindowPlacement p)
    {
        if (p.Width < 200 || p.Height < 150) return false;
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(p.Left, p.Top, p.Width, p.Height);
        rect.Intersect(screen);
        return rect is { Width: >= 120, Height: >= 80 };
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, StrategyWindowPlacement>>(File.ReadAllText(FilePath))
                  ?? new Dictionary<string, StrategyWindowPlacement>()
                : new Dictionary<string, StrategyWindowPlacement>();
        }
        catch
        {
            _cache = new Dictionary<string, StrategyWindowPlacement>();
        }
    }
}
