using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The Blocks SDK as a model is given it: conventions, an index of one line per block, and one card per
/// block that is sent only to the tasks that name it.
///
/// <para><b>Progressive disclosure is the whole saving.</b> The widget SDK's pack went to every call in
/// full — about forty thousand tokens, most of them signatures the task at hand never touched. Here the
/// planner sees the conventions and the index, and each builder sees those plus the three or four cards
/// its task names.</para>
///
/// <para>Read from the committed files embedded at build, not reflected at run time: the reflection
/// needs the SDK's XML documentation next to the assembly, and an installed app does not ship it.
/// <c>BlockCardTests</c> fails when the committed files fall behind the SDK.</para>
/// </summary>
public sealed partial class BlockCatalog
{
    private const string Prefix = "DaxAlgo.AiContext.Blocks.";
    private const string CardPrefix = Prefix + "Card.";

    /// <summary>The block every unit uses: the class itself and its context.</summary>
    public const string UnitBlock = "unit";

    /// <summary>The page bridge.</summary>
    public const string UiBlock = "ui";

    private static readonly Lazy<BlockCatalog> Embedded = new(() => Load(typeof(BlockCatalog).Assembly));

    [GeneratedRegex(@"^<!--.*?-->\s*", RegexOptions.Singleline)]
    private static partial Regex GeneratedBanner();

    [GeneratedRegex(@"^- `(?<id>[\w.]+)`", RegexOptions.Multiline)]
    private static partial Regex IndexEntry();

    /// <summary>The interface a card's calls are on: <c>Calls on `IMarketData`:</c>.</summary>
    [GeneratedRegex(@"^Calls on `(?<name>\w+)`", RegexOptions.Multiline)]
    private static partial Regex CallsOn();

    /// <summary>A top-level entry under a card's <c>Types:</c> — a type, record, enum or constructor. Nested
    /// (indented) lines are its members, and a member's return type is not a name the card declares.</summary>
    [GeneratedRegex(@"^- `(?:new\s+)?(?<name>[A-Z]\w*)", RegexOptions.Multiline)]
    private static partial Regex DeclaredType();

    private readonly Dictionary<string, string> _cards;

    /// <summary>Per block, the interface and type names its card declares.</summary>
    private readonly Dictionary<string, string[]> _names;

    private BlockCatalog(string conventions, string index, Dictionary<string, string> cards)
    {
        Conventions = conventions.Trim();
        Index = GeneratedBanner().Replace(index, string.Empty).Trim();
        _cards = cards;
        _names = cards.ToDictionary(
            c => c.Key,
            c => CallsOn().Matches(c.Value)
                .Concat(DeclaredType().Matches(c.Value.Split("Types:", 2) is [_, var types] ? types : string.Empty))
                .Select(m => m.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);

        // Index order is the catalog's order — unit first, maths near the end — and it is the order
        // cards are sent in, so two tasks naming the same blocks send byte-identical text.
        var ordered = IndexEntry().Matches(Index).Select(m => m.Groups["id"].Value).Where(cards.ContainsKey).ToList();
        ordered.AddRange(cards.Keys.Where(id => !ordered.Contains(id)).Order(StringComparer.Ordinal));
        Ids = ordered;
    }

    /// <summary>How a unit is written — shape, rules, the page, output format.</summary>
    public string Conventions { get; }

    /// <summary>One line per block.</summary>
    public string Index { get; }

    /// <summary>Every block id, in catalog order.</summary>
    public IReadOnlyList<string> Ids { get; }

    /// <summary>
    /// The prefix every call in a Blocks run shares: conventions, then the index.
    ///
    /// <para>Identical for the planner, every builder, every repair and every critic, so a provider's
    /// prompt cache hits on all of them. What differs per task — its cards — goes in the message.</para>
    /// </summary>
    public string SharedContext => Conventions + "\n\n---\n\n" + Index;

    /// <summary>The embedded catalog.</summary>
    public static BlockCatalog Load() => Embedded.Value;

    /// <summary>True when <paramref name="id"/> names a block.</summary>
    public bool Knows(string id) => _cards.ContainsKey(id.Trim());

    /// <summary>One block's card, or null.</summary>
    public string? Card(string id) => _cards.GetValueOrDefault(id.Trim());

    /// <summary>
    /// The known blocks among <paramref name="ids"/>, in catalog order, without duplicates. Unknown names
    /// are dropped: a planner that invents <c>charting</c> has asked for nothing a card can give.
    /// </summary>
    public IReadOnlyList<string> Resolve(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = new HashSet<string>(ids.Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
        return [.. Ids.Where(wanted.Contains)];
    }

    /// <summary>
    /// The blocks <paramref name="text"/> names — by id (<c>math.orderflow</c>), or by an interface or type
    /// its card declares (<c>FootprintTimeBucketer</c>, <c>IInstruments</c>) — in catalog order.
    ///
    /// <para>For a task nobody planned. A brief that says "use TradeClassifier and read the tick size from
    /// the instruments block" has named its cards; a builder sent the five everyday ones instead guesses
    /// those two APIs from their names.</para>
    /// </summary>
    public IReadOnlyList<string> Mentioned(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return [.. Ids.Where(id =>
            Regex.IsMatch(text, $@"(?<![\w.]){Regex.Escape(id)}(?![\w.])", RegexOptions.IgnoreCase)
            || _names[id].Any(name => Regex.IsMatch(text, $@"\b{Regex.Escape(name)}\b")))];
    }

    /// <summary>The cards for <paramref name="ids"/>, joined, in catalog order.</summary>
    public string Compose(IEnumerable<string> ids)
    {
        var text = new StringBuilder();
        foreach (var id in Resolve(ids))
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(_cards[id]);
        }

        return text.ToString();
    }

    private static BlockCatalog Load(System.Reflection.Assembly assembly)
    {
        string Read(string resource)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException(
                    $"Blocks context resource '{resource}' is not embedded — check the EmbeddedResource items in DaxAlgo.Codegen.csproj.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var cards = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(CardPrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .ToDictionary(
                n => n[CardPrefix.Length..^3],
                n => GeneratedBanner().Replace(Read(n), string.Empty).Trim(),
                StringComparer.OrdinalIgnoreCase);

        if (cards.Count == 0)
            throw new InvalidOperationException("No Blocks cards are embedded — check DaxAlgo.Codegen.csproj.");

        return new BlockCatalog(Read(Prefix + "conventions.md"), Read(Prefix + "index.md"), cards);
    }
}
