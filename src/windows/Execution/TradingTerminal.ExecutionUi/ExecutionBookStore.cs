using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// One book as it needs to exist again after a restart.
///
/// <para>This is the user's <em>intent</em> — the book they asked for — not its runtime state.
/// Positions, orders and equity are the ledger's business and are rebuilt by the engine; what a
/// restart must not lose is the fact that the book exists at all.</para>
/// </summary>
/// <param name="Name">Display name, unique within the engine.</param>
/// <param name="AdapterId">The broker adapter the book trades through.</param>
/// <param name="Symbol">The instrument symbol the book was created for.</param>
/// <param name="Strategies">Strategy ids attached at creation. May be empty.</param>
/// <param name="IsPaused">Whether new-order intake was paused when the app last closed.</param>
public sealed record PersistedExecutionBook(
    string Name,
    string AdapterId,
    string Symbol,
    IReadOnlyList<string> Strategies,
    bool IsPaused);

/// <summary>Where the engine's books are remembered between runs.</summary>
public interface IExecutionBookStore
{
    /// <summary>Every remembered book. Empty on a fresh install or an unreadable file.</summary>
    IReadOnlyList<PersistedExecutionBook> Read();

    /// <summary>Replaces the remembered set. Called whenever the engine's books change.</summary>
    void Save(IReadOnlyList<PersistedExecutionBook> books);
}

/// <summary>
/// A JSON file under the user's local application data.
///
/// <para>Plain JSON rather than the order ledger on purpose: a book definition is configuration, not
/// an audit record, and it carries nothing sensitive. Note what is <b>not</b> here — no Paper/Real
/// arming and no live-execution confirmation. Both of those are deliberately unpersisted gates, and a
/// restored book comes back subject to whatever the app-wide switch says today.</para>
///
/// <para>Every failure is swallowed into "no books" or "not saved". Losing the list is annoying;
/// taking the engine down at startup because a file is malformed is worse.</para>
/// </summary>
public sealed class JsonExecutionBookStore : IExecutionBookStore
{
    /// <summary>Bounds the file. The engine caps live books well below this.</summary>
    public const int MaximumBooks = 64;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public JsonExecutionBookStore()
        : this(DefaultPath)
    {
    }

    public JsonExecutionBookStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>Default per-user path, alongside the other execution-owned files.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgoTerminal",
        "Execution",
        "books.json");

    /// <summary>The normalized backing-file path.</summary>
    public string FilePath => _path;

    public IReadOnlyList<PersistedExecutionBook> Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    return [];

                var books = JsonSerializer.Deserialize<PersistedExecutionBook[]>(
                    File.ReadAllText(_path),
                    SerializerOptions);

                return books is null
                    ? []
                    : books.Where(IsUsable).Take(MaximumBooks).ToArray();
            }
            catch (Exception)
            {
                // Malformed, unreadable, or written by a newer build. Start with none rather than
                // refusing to start at all.
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<PersistedExecutionBook> books)
    {
        ArgumentNullException.ThrowIfNull(books);
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var payload = JsonSerializer.Serialize(
                    books.Where(IsUsable).Take(MaximumBooks).ToArray(),
                    SerializerOptions);

                // Write-then-move, so a crash mid-write leaves the previous list intact rather than a
                // truncated file that reads as "no books".
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, payload);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception)
            {
                // A book that fails to persist still works for this session.
            }
        }
    }

    private static bool IsUsable(PersistedExecutionBook book) =>
        book is not null &&
        !string.IsNullOrWhiteSpace(book.Name) &&
        !string.IsNullOrWhiteSpace(book.AdapterId);
}

/// <summary>Remembers nothing. The default when a host composes no store.</summary>
public sealed class NullExecutionBookStore : IExecutionBookStore
{
    public static NullExecutionBookStore Instance { get; } = new();

    public IReadOnlyList<PersistedExecutionBook> Read() => [];

    public void Save(IReadOnlyList<PersistedExecutionBook> books)
    {
    }
}
