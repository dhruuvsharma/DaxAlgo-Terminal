using System.Text;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>One type as the generated surface wrote it: where it belongs, what it is called, what a
/// brief might say to mean it, and the block of markdown that documents it.</summary>
/// <param name="Name">The type name.</param>
/// <param name="Section">Which section of the surface it was written under.</param>
/// <param name="Terms">Derived search terms — the name split on camel case, the identifiers in the
/// signature block, and the words of the lead summary. Derived from the block rather than written
/// beside it: a hand-maintained keyword list next to a generated document is right on the day it is
/// written and silently wrong after the next rename, which is the drift the generator exists to
/// prevent. Carrying them in the markers instead cost 26 KB of the document, so they are computed
/// here from text that is already in hand.</param>
/// <param name="Body">The markdown block, exactly as generated.</param>
/// <param name="IsCompact">True when the generator already reduced this type to a single line (an
/// options record). There is nothing left to ration, so it is always written in full.</param>
public sealed record SdkSurfaceType(
    string Name, string Section, IReadOnlyList<string> Terms, string Body, bool IsCompact)
{
    /// <summary>How well a brief calls for this type — the number of its distinct terms the brief
    /// mentions. A count rather than a boolean, so "footprint imbalance delta" outranks a type that
    /// merely shares the word "price" with everything else in the library.</summary>
    public int Score(IReadOnlyCollection<string> briefWords)
    {
        var hits = 0;
        foreach (var term in Terms)
        {
            if (briefWords.Contains(term)) hits++;
        }
        return hits;
    }

    /// <summary>
    /// The one-line entry a type gets when the budget cannot afford its full block: its name and
    /// enough of what it is for to decide whether to ask about it.
    ///
    /// <para><b>Capped, and the cap earns its keep.</b> Sixty-odd of these are written on every cut
    /// prompt, so the difference between a first sentence and a clipped clause is thousands of
    /// characters — at full sentences the index was giving back a third of everything the cut had
    /// saved. What a model needs here is not a definition; it is enough to know whether this is the
    /// thing it wants.</para>
    /// </summary>
    public string IndexLine()
    {
        const int cap = 72;

        foreach (var line in Body.Replace("\r\n", "\n").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith("###", StringComparison.Ordinal)) continue;
            if (text.StartsWith("```", StringComparison.Ordinal)) break;

            var stop = text.IndexOf(". ", StringComparison.Ordinal);
            var lead = stop < 0 ? text : text[..stop];
            if (lead.Length > cap)
            {
                // Cut at a word boundary; a truncated word reads as a typo rather than an ellipsis.
                var space = lead.LastIndexOf(' ', cap);
                lead = (space > cap / 2 ? lead[..space] : lead[..cap]) + "…";
            }

            return $"- `{Name}` — {lead}";
        }

        return $"- `{Name}`";
    }
}

/// <summary>
/// Cuts the generated SDK surface down to what one brief actually needs.
///
/// <para><b>Why.</b> The surface is the bulk of the system prompt — 76 KB of a ~110 KB Deep-effort
/// prompt — and it is reflected from the SDK, so it grows every time the SDK does. Measured against
/// NVIDIA NIM, 67 KB reached first byte at 278 s and 83 KB returned a 504 at 302 s, because the
/// gateway drops a connection idle for around 300 s and a reasoning model emits nothing while it
/// reasons. This is not a provider to switch away from; it is the harness outgrowing every provider.
/// A brief about an order book does not need the whole SDK reflected at it.</para>
///
/// <para><b>What is never cut.</b> The three contract sections — what you implement, what you draw
/// onto, and the vocabulary they are written in — go through whole, always. Only the two
/// <i>libraries</i> are rationed, and even they keep a one-line entry for every type they hold.</para>
///
/// <para><b>The failure mode is deliberately gentler than the skills'.</b>
/// <c>StrategySkillLibrary</c> skips a whole pack when it does not fit, silently, which is why
/// <c>SkillBudgetTests</c> has to pin the arithmetic. Here nothing ever disappears: a type the budget
/// cannot afford keeps its name and its first sentence, so the model still knows it exists and can ask
/// for it. Rationing detail degrades; rationing existence misleads — a model that cannot see
/// <c>Ladder</c> does not use a worse ladder, it writes one.</para>
///
/// <para><b>Spare budget is never wasted.</b> Once every type the brief named has been written in
/// full, the remainder is spent on the rest in a stable order, so a short brief against a small SDK
/// simply gets everything and this class costs nothing. It starts mattering exactly when the library
/// grows, which is the problem it was built for.</para>
/// </summary>
public static class SdkSurfaceSelector
{
    /// <summary>
    /// Characters of <i>library detail</i> one prompt may carry — the two helper sections only; the
    /// contract sections are additional and unrationed.
    ///
    /// <para>Sized to be a real cut rather than a gesture: the two libraries are about 48,000
    /// characters together, so this is roughly half of them, and the half chosen is the half the brief
    /// asked about. A budget that admitted almost everything would leave the prompt where it is while
    /// adding a mechanism to explain.</para>
    ///
    /// <para><b>Measured 2026-08-31</b>, on "an order book depth ladder with a liquidity heatmap and
    /// cumulative delta", against a 74,830-character surface holding 60 rationed library types:</para>
    ///
    /// <code>
    ///  budget   surface   types in full
    ///   8,000    41,344   15
    ///  16,000    49,372   28
    ///  24,000    57,046   43     &lt;- here
    ///  32,000    65,295   53
    ///  48,000    74,830   60     (everything; the budget stops binding)
    /// </code>
    ///
    /// <para>Move it with that table rather than by feel. The knee is around here: below 16,000 the
    /// library stops being a library, and above 32,000 the cut stops paying for itself.</para>
    ///
    /// <para><b>Do not expect this alone to clear the gateway.</b> It takes a Deep-effort prompt from
    /// 112,219 to 94,435 characters — a real 16% — and the wall that prompted the work is at roughly
    /// 67,000. The surface is no longer the single largest thing in the prompt: after the cut it is
    /// 57,000, and the rest is the conventions (10,658), the worked exemplar (about 11,000) and the
    /// domain packs (up to 18,000).</para>
    ///
    /// <para><b>Measured again 2026-09-04</b>, on a UI-heavy brief (three-candle triangles, area
    /// comparison, a three-panel window) at Standard, to find where the next bite should come from.
    /// Surface 121,539 cut to 81,615; conventions 11,828; joined 93,450, dividing like this:</para>
    ///
    /// <code>
    ///  38,230  Vocabulary          &lt;- largest, and NOT rationed
    ///  19,681  Drawing helpers     &lt;- rationed
    ///  12,836  Quant helpers       &lt;- rationed
    ///   6,874  What you implement  }
    ///   4,999  Output contract     }  contract sections, never cut
    ///   4,757  What you draw onto  }
    /// </code>
    ///
    /// <para><b>The next bite is not there, and that is the finding.</b> Vocabulary is larger than both
    /// rationed libraries together, so it looks like the obvious target — and it is 46 entries
    /// averaging 820 characters, every one a type an author writes against: <c>OhlcvBar</c>,
    /// <c>Quote</c>, <c>TradePrint</c>, <c>InstrumentId</c>, <c>UnitLayout</c>, <c>StrategyParameter</c>.
    /// Rationing it would reduce <c>OhlcvBar</c> to a one-line entry, and a brief about candle highs and
    /// lows needs its members. <c>SdkSurfaceGenerator.HelperSections</c> already says this ("the
    /// vocabulary those two are written in ... never rationed however long the brief is"); the
    /// measurement confirms it rather than overturning it.</para>
    ///
    /// <para>So the only remaining lever here is this budget, worth about 7,700 characters of a
    /// 121,000-character prompt — six per cent, against a real risk of degrading a configuration
    /// measured to work. <b>And the prompt is not what makes a slow model slow:</b> the same brief on
    /// z-ai/glm-5.3-free spent 14m32s reasoning before its first output character, then compiled
    /// cleanly in one generation. Whoever wants that quarter of an hour back should spend it on the
    /// model or on prompt caching — that route reports <c>cached=0</c>, so every turn re-pays for the
    /// entire prompt — and not on cutting the surface further.</para>
    ///
    /// <para><c>SdkSurfaceSelectionTests</c> pins both directions: that a real brief is cut, and that
    /// the types it named survive the cut.</para>
    /// </summary>
    public const int MaxCharacters = 24_000;

    /// <summary>
    /// The surface, cut to <paramref name="brief"/>, and always without the generator's boundary
    /// markers — those are for this class to read, never for a model.
    ///
    /// <para>A null or empty brief keeps every type in full: there is nothing to be relevant to, and
    /// guessing would be worse than spending the tokens.</para>
    /// </summary>
    public static string For(string surface, string? brief, int maxCharacters = MaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var (preamble, types) = Parse(surface);
        if (types.Count == 0) return surface;

        var chosen = string.IsNullOrWhiteSpace(brief) || maxCharacters <= 0
            ? types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)
            : Choose(types, brief, maxCharacters);

        return Compose(preamble, types, chosen);
    }

    /// <summary>The types the surface documents, in the order it documents them. Public so a test can
    /// assert on the parse rather than on a rendered string.</summary>
    public static IReadOnlyList<SdkSurfaceType> TypesIn(string surface) => Parse(surface).Types;

    /// <summary>The names whose full blocks a brief earns, within the budget.</summary>
    public static IReadOnlySet<string> Detailed(
        string surface, string? brief, int maxCharacters = MaxCharacters)
    {
        if (string.IsNullOrWhiteSpace(brief)) return Parse(surface).Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        return Choose(Parse(surface).Types, brief, maxCharacters);
    }

    private static HashSet<string> Choose(
        IReadOnlyList<SdkSurfaceType> types, string brief, int maxCharacters)
    {
        var words = Tokenise(brief);

        // Contract sections and already-compact entries are not candidates: they are written in full
        // whatever the brief says, so spending budget on them would ration the libraries twice.
        var candidates = types
            .Where(t => SdkSurfaceGenerator.HelperSections.Contains(t.Section) && !t.IsCompact)
            .Select(t => (Type: t, Score: t.Score(words)))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Type.Body.Length)                              // cheap wins first at equal relevance
            .ThenBy(c => c.Type.Name, StringComparer.Ordinal)             // stable: same brief, same prompt
            .ToArray();

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        var budget = maxCharacters;

        foreach (var (type, _) in candidates)
        {
            // Skipped rather than stopped at: a large type that does not fit must not deny the budget
            // to every smaller one behind it.
            if (type.Body.Length > budget) continue;

            chosen.Add(type.Name);
            budget -= type.Body.Length;
        }

        return chosen;
    }

    private static string Compose(
        string preamble, IReadOnlyList<SdkSurfaceType> types, IReadOnlySet<string> chosen)
    {
        var markdown = new StringBuilder(preamble.TrimEnd()).AppendLine().AppendLine();

        foreach (var section in SdkSurfaceGenerator.SectionOrder)
        {
            var members = types.Where(t => t.Section == section).ToArray();
            if (members.Length == 0) continue;

            var rationed = SdkSurfaceGenerator.HelperSections.Contains(section);
            markdown.AppendLine($"## {section}").AppendLine();

            // The index goes FIRST, and lists the whole section including the types written out below
            // it. A model scanning for "is there something for depth" gets one place to look, and the
            // answer does not depend on which half of the section it landed in.
            if (rationed && members.Any(m => !chosen.Contains(m.Name) && !m.IsCompact))
            {
                markdown.AppendLine(
                    "Everything in this section, one line each. The ones this brief calls for are "
                    + "written out in full below; ask for any of the others by name and they will be.")
                    .AppendLine();

                foreach (var member in members.Where(m => !m.IsCompact))
                    markdown.AppendLine(member.IndexLine());

                markdown.AppendLine();
            }

            foreach (var member in members)
            {
                if (rationed && !member.IsCompact && !chosen.Contains(member.Name)) continue;
                markdown.AppendLine(member.Body.TrimEnd()).AppendLine();
            }
        }

        return markdown.ToString();
    }

    /// <summary>Splits the document back into its types on the generator's boundary markers.</summary>
    private static (string Preamble, IReadOnlyList<SdkSurfaceType> Types) Parse(string surface)
    {
        var lines = surface.Replace("\r\n", "\n").Split('\n');
        var preamble = new StringBuilder();
        var types = new List<SdkSurfaceType>();

        string? name = null, section = null;
        var body = new StringBuilder();
        var started = false;

        void Flush()
        {
            if (name is null || section is null) return;

            var text = body.ToString().Trim();
            types.Add(new SdkSurfaceType(
                name, section, TermsFor(name, text), text,
                IsCompact: !text.StartsWith("###", StringComparison.Ordinal)));
            body.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith(SdkSurfaceGenerator.MarkerPrefix, StringComparison.Ordinal))
            {
                Flush();
                started = true;

                // "<!-- @type Name | Section | term term term -->"
                var inner = line[SdkSurfaceGenerator.MarkerPrefix.Length..];
                var end = inner.LastIndexOf("-->", StringComparison.Ordinal);
                if (end >= 0) inner = inner[..end];

                var parts = inner.Split('|', 2);
                name = parts[0].Trim();
                section = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                continue;
            }

            // Section headings are re-emitted from the markers, so they are dropped here rather than
            // trailing whichever type happened to precede them — a type whose detail is cut would
            // otherwise take the next section's heading down with it.
            if (line.StartsWith("## ", StringComparison.Ordinal)) continue;

            if (started) body.AppendLine(line);
            else preamble.AppendLine(line);
        }

        Flush();
        return (preamble.ToString(), types);
    }

    /// <summary>
    /// The words a brief could plausibly use to mean this type: the name split on camel case, the
    /// identifiers in its signature block, and the words of its lead paragraph.
    ///
    /// <para>Stops at the signature fence rather than reading the whole block. Every member's own
    /// paragraph would drag in the common vocabulary of the entire library — "price", "value",
    /// "update" — and a brief mentioning price would then score every type equally, which is the same
    /// as scoring none of them.</para>
    /// </summary>
    private static IReadOnlyList<string> TermsFor(string name, string body)
    {
        var terms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var word in SplitIdentifier(name)) terms.Add(word);

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var inFence = false;

        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                // Past the signatures, the rest is per-member prose. Stop.
                if (inFence) break;
                inFence = true;
                continue;
            }

            if (text.Length == 0 || text.StartsWith("###", StringComparison.Ordinal)) continue;

            if (inFence)
            {
                // A signature line: every identifier in it is a name a brief might use.
                foreach (var token in text.Split(
                             [' ', '(', ')', '<', '>', ',', '{', '}', ';', '[', ']', '='],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var word in SplitIdentifier(token)) terms.Add(word);
                }
            }
            else
            {
                foreach (var word in Prose(text)) terms.Add(word);
            }
        }

        return [.. terms];
    }

    /// <summary>Camel-case identifier to lower-cased words: <c>OrderFlowImbalance</c> becomes
    /// order, flow, imbalance.</summary>
    private static IEnumerable<string> SplitIdentifier(string identifier)
    {
        var start = 0;
        for (var i = 1; i <= identifier.Length; i++)
        {
            if (i != identifier.Length && !char.IsUpper(identifier[i])) continue;

            var word = identifier[start..i].Trim();
            if (word.Length > 2 && word.All(char.IsAsciiLetter)) yield return word.ToLowerInvariant();
            start = i;
        }
    }

    /// <summary>Prose to lower-cased words worth searching on. Short ones are dropped: a brief
    /// containing "the" must not pull in every type whose summary contains "the".</summary>
    private static IEnumerable<string> Prose(string text)
    {
        foreach (var raw in text.Split(
                     [' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '<', '>', '`', '"', '\'', '/', '—', '-', '*'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.ToLowerInvariant();
            if (word.Length > 3 && word.All(char.IsAsciiLetter)) yield return word;
        }
    }

    /// <summary>The brief as a set of lower-cased words, matching how the terms are derived — so
    /// "order-flow imbalance" finds <c>OrderFlowImbalance</c>.</summary>
    private static HashSet<string> Tokenise(string brief)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in brief.Split(
                     [' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '<', '>', '`', '"', '\'', '/', '—', '-', '_'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.ToLowerInvariant().Trim();
            if (word.Length > 2) words.Add(word);

            // A plural in the brief must find the singular type: "candles" is Candles but "ladders"
            // is Ladder, and a brief writes whichever reads naturally.
            if (word.Length > 3 && word.EndsWith('s')) words.Add(word[..^1]);
        }

        return words;
    }
}
