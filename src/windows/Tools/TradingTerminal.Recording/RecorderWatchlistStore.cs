using System.IO;
using System.Text.Json;

namespace TradingTerminal.Recording;

/// <summary>One remembered row of the recorder watchlist: the canonical <c>Contract.Symbol</c> plus the
/// broker the user pinned it to (null = "whichever broker is connected"). Symbol-keyed for the same
/// reason <see cref="TradingTerminal.UI.LastInstrumentStore"/> is: it re-matches against whatever
/// universe is live at reload (registry, fallback, or a connected broker's list).</summary>
public sealed record RecorderWatchlistItem(string Symbol, string? Broker);

/// <summary>The whole persisted recorder state — what to record and the upload preferences.
/// <c>IsRecording</c> is deliberately NOT persisted: pumps need a connected broker, and at app start
/// the login hasn't happened yet, so a resumed recording would just fail. The user re-arms it.</summary>
public sealed record RecorderWatchlist(
    IReadOnlyList<RecorderWatchlistItem> Items,
    bool AutoUploadTelegram,
    bool DeleteLocalAfterUpload)
{
    public static RecorderWatchlist Empty { get; } = new(Array.Empty<RecorderWatchlistItem>(), false, false);
}

/// <summary>
/// Best-effort, file-backed persistence for the recorder watchlist. Mirrors
/// <see cref="TradingTerminal.UI.LastInstrumentStore"/>: any IO/JSON failure is swallowed, and it lives
/// under the same <c>%LOCALAPPDATA%/DaxAlgo Terminal</c> root as the rest of the user's files. Losing a
/// watchlist must never be louder than a missing convenience.
/// </summary>
public static class RecorderWatchlistStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal", "recorder-watchlist.json");

    private static readonly object Gate = new();

    public static RecorderWatchlist Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return RecorderWatchlist.Empty;
                return JsonSerializer.Deserialize<RecorderWatchlist>(File.ReadAllText(FilePath))
                       ?? RecorderWatchlist.Empty;
            }
            catch
            {
                return RecorderWatchlist.Empty;
            }
        }
    }

    public static void Save(RecorderWatchlist watchlist)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(watchlist, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // best-effort: an unwritable profile dir must not break recording or app close.
            }
        }
    }
}
