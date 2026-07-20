namespace TradingTerminal.App.Authoring;

/// <summary>One line of a rendered diff: <c>Kind</c> is "add", "del" or "ctx" (string, not an enum, so
/// the duck-typed XAML templates can trigger on it without referencing this assembly).</summary>
public sealed record DiffLine(string Kind, string Text);

/// <summary>
/// Minimal line-level LCS diff for the builder: per-turn "+N −M" file chips, and the review gate's
/// per-file diff. Strategy sources are a few hundred lines, so a plain O(n·m) LCS table is fine; a
/// pathological input (the table would exceed ~2M cells) degrades to "everything changed" rather than
/// burning memory — the counts stay honest, just coarse. No external diff package on purpose: this
/// project is portable (WPF-free) and the need is exactly this small.
/// </summary>
public static class LineDiff
{
    private const long MaxCells = 2_000_000;

    /// <summary>Lines added/removed going from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static (int Added, int Removed) Count(string before, string after)
    {
        var lines = Build(before, after);
        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            if (line.Kind == "add") added++;
            else if (line.Kind == "del") removed++;
        }

        return (added, removed);
    }

    /// <summary>The full diff, oldest-first: context, deletions, then additions per hunk.</summary>
    public static IReadOnlyList<DiffLine> Build(string before, string after)
    {
        var a = Split(before);
        var b = Split(after);

        if (a.Length == 0 && b.Length == 0) return [];
        if (string.Equals(before, after, StringComparison.Ordinal))
            return [.. a.Select(l => new DiffLine("ctx", l))];

        if ((long)a.Length * b.Length > MaxCells)
        {
            var coarse = new List<DiffLine>(a.Length + b.Length);
            coarse.AddRange(a.Select(l => new DiffLine("del", l)));
            coarse.AddRange(b.Select(l => new DiffLine("add", l)));
            return coarse;
        }

        // Classic LCS table + backtrack. Rows/cols are offset by one so [0,*]/[*,0] are the empty
        // prefixes; the backtrack emits deletions before additions inside a hunk (git's ordering).
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                table[i, j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        var reversed = new List<DiffLine>(a.Length + b.Length);
        var (x, y) = (a.Length, b.Length);
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && string.Equals(a[x - 1], b[y - 1], StringComparison.Ordinal))
            {
                reversed.Add(new DiffLine("ctx", a[x - 1]));
                x--;
                y--;
            }
            else if (y > 0 && (x == 0 || table[x, y - 1] >= table[x - 1, y]))
            {
                reversed.Add(new DiffLine("add", b[y - 1]));
                y--;
            }
            else
            {
                reversed.Add(new DiffLine("del", a[x - 1]));
                x--;
            }
        }

        reversed.Reverse();
        return reversed;
    }

    private static string[] Split(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
