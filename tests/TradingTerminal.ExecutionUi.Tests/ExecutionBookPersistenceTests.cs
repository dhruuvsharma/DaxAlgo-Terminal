using System.IO;
using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// Books outlive the process. Closing the application used to lose every book the user had made,
/// because the engine held them in memory and nothing wrote them down.
/// </summary>
public sealed class ExecutionBookPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "daxalgo-book-store-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void ABookSurvivesARoundTrip()
    {
        var path = Path.Combine(_directory, "books.json");
        var store = new JsonExecutionBookStore(path);

        store.Save([new PersistedExecutionBook("Alpha", "simulated", "ESZ5", ["sigma"], IsPaused: false)]);

        var reloaded = new JsonExecutionBookStore(path).Read();

        var book = Assert.Single(reloaded);
        Assert.Equal("Alpha", book.Name);
        Assert.Equal("simulated", book.AdapterId);
        Assert.Equal("ESZ5", book.Symbol);
        Assert.Equal(["sigma"], book.Strategies);
        Assert.False(book.IsPaused);
    }

    [Fact]
    public void APausedBookComesBackPaused()
    {
        // Run/Stop is part of what a book IS. A book the user stopped must not quietly start taking
        // orders again because the application restarted.
        var path = Path.Combine(_directory, "books.json");
        new JsonExecutionBookStore(path).Save(
            [new PersistedExecutionBook("Alpha", "simulated", "ESZ5", [], IsPaused: true)]);

        Assert.True(Assert.Single(new JsonExecutionBookStore(path).Read()).IsPaused);
    }

    [Fact]
    public void NothingIsRememberedOnAFreshInstall()
    {
        Assert.Empty(new JsonExecutionBookStore(Path.Combine(_directory, "absent.json")).Read());
    }

    [Fact]
    public void ACorruptFileReadsAsNoBooksRatherThanThrowing()
    {
        // Refusing to start because a settings file is malformed is worse than starting with none.
        var path = Path.Combine(_directory, "books.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        Assert.Empty(new JsonExecutionBookStore(path).Read());
    }

    [Fact]
    public void BooksMissingTheFieldsNeededToRecreateThemAreDropped()
    {
        // A book with no adapter cannot be recreated, so remembering it only produces a failure on
        // every launch.
        var path = Path.Combine(_directory, "books.json");
        var store = new JsonExecutionBookStore(path);

        store.Save(
        [
            new PersistedExecutionBook("Good", "simulated", "ESZ5", [], false),
            new PersistedExecutionBook("", "simulated", "ESZ5", [], false),
            new PersistedExecutionBook("No adapter", "", "ESZ5", [], false),
        ]);

        Assert.Equal(["Good"], store.Read().Select(book => book.Name));
    }

    [Fact]
    public void SavingReplacesRatherThanAppends()
    {
        var path = Path.Combine(_directory, "books.json");
        var store = new JsonExecutionBookStore(path);

        store.Save([new PersistedExecutionBook("Alpha", "simulated", "ESZ5", [], false)]);
        store.Save([new PersistedExecutionBook("Beta", "simulated", "NQZ5", [], false)]);

        Assert.Equal(["Beta"], store.Read().Select(book => book.Name));
    }

    [Fact]
    public void TheFileIsBounded()
    {
        // A store that grows forever eventually costs a slow startup for books nobody has.
        var path = Path.Combine(_directory, "books.json");
        var store = new JsonExecutionBookStore(path);

        store.Save(Enumerable
            .Range(0, JsonExecutionBookStore.MaximumBooks + 40)
            .Select(index => new PersistedExecutionBook($"Book {index}", "simulated", "ESZ5", [], false))
            .ToArray());

        Assert.Equal(JsonExecutionBookStore.MaximumBooks, store.Read().Count);
    }

    [Fact]
    public void APartiallyWrittenFileNeverReplacesAGoodOne()
    {
        // Save writes to a temporary file and moves it into place, so a crash mid-write leaves the
        // previous list intact instead of a truncated one that reads as "no books".
        var path = Path.Combine(_directory, "books.json");
        var store = new JsonExecutionBookStore(path);
        store.Save([new PersistedExecutionBook("Alpha", "simulated", "ESZ5", [], false)]);

        Assert.False(File.Exists(path + ".tmp"), "the temporary file must not be left behind");
        Assert.Equal(["Alpha"], store.Read().Select(book => book.Name));
    }

    // -- End to end, through the engine ---------------------------------------------------------

    [Fact]
    public async Task ABookMadeInOneSessionIsThereInTheNext()
    {
        // The actual bug: create a book, close the application, reopen it, and the book is gone.
        var store = new JsonExecutionBookStore(Path.Combine(_directory, "books.json"));

        using (var first = new InProcessExecutionClient(bookStore: store))
        {
            var created = await first.CreateBookAsync(
                new ExecutionBookCreateRequest("Alpha", "paper", Array.AsReadOnly(["Test strategy"])));
            Assert.True(created.IsSuccess, created.Message);
        }

        using var second = new InProcessExecutionClient(bookStore: store);
        Assert.Empty(second.GetSnapshot().Books);

        var restored = await second.RestoreBooksAsync();

        Assert.True(restored.IsSuccess, restored.Message);
        Assert.Equal("Alpha", Assert.Single(second.GetSnapshot().Books).Name);
    }

    [Fact]
    public async Task AStoppedBookIsStillStoppedInTheNextSession()
    {
        var store = new JsonExecutionBookStore(Path.Combine(_directory, "books.json"));

        using (var first = new InProcessExecutionClient(bookStore: store))
        {
            await first.CreateBookAsync(
                new ExecutionBookCreateRequest("Alpha", "paper", Array.AsReadOnly(["Test strategy"])));
            var bookId = Assert.Single(first.GetSnapshot().Books).Id;
            var paused = await first.SetIntakePausedAsync(bookId, paused: true);
            Assert.True(paused.IsSuccess, paused.Message);
        }

        using var second = new InProcessExecutionClient(bookStore: store);
        await second.RestoreBooksAsync();

        // A book the user stopped must not start taking orders again just because the app restarted.
        Assert.True(Assert.Single(second.GetSnapshot().Books).IsIntakePaused);
    }

    [Fact]
    public async Task RestoringTwiceDoesNotDuplicateABook()
    {
        var store = new JsonExecutionBookStore(Path.Combine(_directory, "books.json"));
        store.Save([new PersistedExecutionBook("Alpha", "paper", string.Empty, [], false)]);

        using var client = new InProcessExecutionClient(bookStore: store);
        await client.RestoreBooksAsync();
        await client.RestoreBooksAsync();

        Assert.Single(client.GetSnapshot().Books);
    }

    [Fact]
    public async Task AnEngineWithNoStoreBehavesExactlyAsBefore()
    {
        using var client = new InProcessExecutionClient();

        var restored = await client.RestoreBooksAsync();

        Assert.True(restored.IsSuccess);
        Assert.Empty(client.GetSnapshot().Books);
    }

    [Fact]
    public void TheNullStoreRemembersNothing()
    {
        NullExecutionBookStore.Instance.Save([new PersistedExecutionBook("Alpha", "simulated", "ESZ5", [], false)]);

        Assert.Empty(NullExecutionBookStore.Instance.Read());
    }

    [Fact]
    public void TheDefaultPathIsUnderTheUsersLocalApplicationData()
    {
        // Per-user, alongside the other execution-owned files — not next to the executable, which a
        // per-machine install makes read-only.
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            JsonExecutionBookStore.DefaultPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("books.json", JsonExecutionBookStore.DefaultPath, StringComparison.Ordinal);
    }
}
