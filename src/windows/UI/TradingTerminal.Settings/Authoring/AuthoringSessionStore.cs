using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>One bubble as the user saw it. Kept separately from the model thread because they are not the
/// same thing: the thread also carries the compiler's auto-fix prompts, which the user never typed and
/// should not have to read.</summary>
public sealed record AuthoringChatEntry(
    string Role,
    string Text,
    DateTime TimestampLocal,
    // The agent-workspace transcript kinds (issue #29). All optional so pre-redesign session files
    // keep deserializing: a null Kind is a plain user/assistant/system bubble.
    string? Kind = null,
    string? State = null,
    string? Detail = null)
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string System = "system";
}

/// <summary>
/// A whole authoring session, as it stood when it was last touched: the chat the user reads, the thread
/// the MODEL reads (so a resumed conversation still remembers what it wrote), the files, the provider
/// setup and what it has cost so far.
/// </summary>
public sealed record AuthoringSessionSnapshot(
    string StrategyId,
    string DisplayName,
    IReadOnlyList<AuthoringChatEntry> Chat,
    IReadOnlyList<CodegenMessage> Thread,
    IReadOnlyList<StrategyFile> Files,
    string? ProviderId = null,
    string? Model = null,
    string? Effort = null,
    string? BuildEffort = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    bool Registered = false,
    DateTime UpdatedUtc = default)
{
    /// <summary>"2 hours ago" — what the session picker shows next to the name.</summary>
    public string Age
    {
        get
        {
            var elapsed = DateTime.UtcNow - UpdatedUtc;
            if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
            if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
            return $"{(int)elapsed.TotalDays}d ago";
        }
    }

    public string Label => $"{DisplayName} ({StrategyId}) · {Age}";

    /// <summary>
    /// Which date bucket the rail files this session under: "Today", "Yesterday", "Previous 7 days",
    /// "Older".
    ///
    /// <para>A flat newest-first list is fine at five sessions and useless at fifty — you scroll past
    /// the one you wanted because nothing tells you where last week starts. Every editor's history
    /// pane groups by recency for that reason, and the grouping is computed HERE rather than in the
    /// view because <c>UpdatedUtc</c> is the only thing that decides it.</para>
    ///
    /// <para>Compared in LOCAL time: a session saved at 23:50 must say "Today" to the person who saved
    /// it, whatever UTC thinks the date is.</para>
    /// </summary>
    public string Group
    {
        get
        {
            var today = DateTime.Now.Date;
            var day = UpdatedUtc == default ? today : UpdatedUtc.ToLocalTime().Date;
            if (day >= today) return "Today";
            if (day >= today.AddDays(-1)) return "Yesterday";
            if (day >= today.AddDays(-7)) return "Previous 7 days";
            return "Older";
        }
    }

    /// <summary>How many times the user has spoken in this session. Assistant turns are deliberately
    /// not counted: one brief that produced nine tool rows is one turn of work, not ten.</summary>
    public int TurnCount => Chat?.Count(entry =>
        string.Equals(entry.Role, AuthoringChatEntry.User, StringComparison.OrdinalIgnoreCase)) ?? 0;

    /// <summary>"3 turns" — the rail's mono meta line.</summary>
    public string TurnLabel => TurnCount switch
    {
        0 => "no turns",
        1 => "1 turn",
        var n => $"{n} turns",
    };

    /// <summary>
    /// The first thing the user actually asked for, flattened to one line.
    ///
    /// <para>This is what makes the history usable. A strategy is named from the brief only after the
    /// first turn, so a rail of un-run sessions is a column of "My custom strategy" — indistinguishable
    /// rows for distinct conversations. The brief is what people remember them by.</para>
    /// </summary>
    public string Summary
    {
        get
        {
            var first = Chat?.FirstOrDefault(entry =>
                string.Equals(entry.Role, AuthoringChatEntry.User, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Text));
            if (first is null) return string.Empty;

            var flat = string.Join(' ', first.Text.Split(
                ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return flat.Length <= 110 ? flat : string.Concat(flat.AsSpan(0, 109).TrimEnd(), "…");
        }
    }

    /// <summary>True when <paramref name="query"/> appears in the name, the id or the opening brief.
    /// Empty matches everything, so an untouched search box hides nothing.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var needle = query.Trim();
        return DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || StrategyId.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || Summary.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Persists authoring sessions to <c>%LocalAppData%\DaxAlgo Terminal\authoring\</c>, one JSON file per
/// strategy id. A strategy is often several sittings' work — a brief, the model's questions, a few
/// rounds of fixes — and losing all of that to a restart (which is what happened before this existed)
/// makes the builder unusable for anything serious.
/// <para>
/// Nothing here is secret: the transcript, the code, and the provider/model choice. API keys live in the
/// DPAPI credential store and never come near this file.
/// </para>
/// </summary>
public static class AuthoringSessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Where saved chats live: <c>%LocalAppData%\DaxAlgo Terminal\authoring\</c>.
    ///
    /// <para>Settable so a test can redirect it, and that is not hygiene — it is a bug that already
    /// happened. A suite driving the real view-model runs turns, and a turn calls <c>Save()</c> in its
    /// finally, so the tests wrote their fixtures into the developer's own chat list. It was found by
    /// rendering the composer to a PNG and seeing "Test strategy" sitting in the session rail beside
    /// real work. <see cref="AiCodegenUserFile.Path"/> carries the same redirect for the same reason,
    /// discovered the same way.</para>
    ///
    /// <para>Computed rather than initialised, so it cannot be broken by reordering the declarations
    /// above it — see that file for the startup crash that pattern prevents. Setting null restores the
    /// default.</para>
    /// </summary>
    public static string Directory
    {
        get => _redirect ?? DefaultDirectory;
        set => _redirect = value;
    }

    private static string? _redirect;

    /// <summary>The real per-user location, kept separately so a test can put <see cref="Directory"/>
    /// back.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DaxAlgo Terminal",
        "authoring");

    /// <summary>Writes the session. A failure is swallowed (and reported to the caller as false) — a
    /// read-only profile must not take the chat down with it.</summary>
    public static bool Save(AuthoringSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.StrategyId)) return false;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(
                PathFor(session.StrategyId),
                JsonSerializer.Serialize(session with { UpdatedUtc = DateTime.UtcNow }, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Every saved session, newest first. A corrupt file is skipped, not thrown — one bad file
    /// must not hide the rest of the user's work.</summary>
    public static IReadOnlyList<AuthoringSessionSnapshot> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var sessions = new List<AuthoringSessionSnapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            if (TryRead(file) is { } session) sessions.Add(session);
        }

        return [.. sessions.OrderByDescending(s => s.UpdatedUtc)];
    }

    public static AuthoringSessionSnapshot? Load(string strategyId) =>
        string.IsNullOrWhiteSpace(strategyId) ? null : TryRead(PathFor(strategyId));

    public static void Delete(string strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId)) return;

        try
        {
            File.Delete(PathFor(strategyId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do — the session simply stays in the list until the next start.
        }
    }

    private static AuthoringSessionSnapshot? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AuthoringSessionSnapshot>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A strategy id is user input and becomes a file name — anything that isn't a letter, digit,
    /// dot, dash or underscore is replaced, so an id can never escape the folder.</summary>
    private static string PathFor(string strategyId)
    {
        var safe = new string(strategyId
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
            .ToArray());
        return Path.Combine(Directory, $"{safe}.json");
    }
}
