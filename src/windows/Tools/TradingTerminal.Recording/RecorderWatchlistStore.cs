using System.IO;
using System.Text.Json;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;

namespace TradingTerminal.Recording;

/// <summary>
/// One remembered row of the recorder watchlist.
///
/// <para>The whole <see cref="SignalInstrument"/> is persisted — display name, category, contract and
/// broker — rather than just a symbol to re-match at load. Symbol-matching (the
/// <see cref="TradingTerminal.UI.LastInstrumentStore"/> approach) is right for a picker that reopens
/// against whatever universe is live, but wrong here: the recorder's rows come from a <i>connected
/// broker's</i> universe, and at app start no broker is connected yet, so a re-match would silently
/// drop every broker-sourced row from the user's watchlist.</para>
/// </summary>
public sealed record RecorderWatchlistItem(
    string Symbol,
    string DisplayName,
    string Category,
    string SecType,
    string Exchange,
    string Currency,
    string PrimaryExchange,
    string? Broker)
{
    public static RecorderWatchlistItem From(SignalInstrument instrument, BrokerKind? pinned)
    {
        var c = instrument.Contract;
        return new RecorderWatchlistItem(
            c.Symbol, instrument.DisplayName, instrument.Category,
            c.SecType, c.Exchange, c.Currency, c.PrimaryExchange,
            pinned?.ToString());
    }

    public SignalInstrument ToInstrument() => new(
        DisplayName,
        Category,
        new Contract(Symbol, SecType, Exchange, Currency, PrimaryExchange),
        Enum.TryParse<BrokerKind>(Broker, out var b) ? b : null);

    public BrokerKind? PinnedBroker => Enum.TryParse<BrokerKind>(Broker, out var b) ? b : null;
}

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
