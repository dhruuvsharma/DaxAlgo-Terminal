using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Renders the small slice of Markdown a model actually writes, into a <see cref="TextBlock"/>.
///
/// <para><b>The pane was showing the syntax.</b> A reply reading "**The maths** — `Imbalance.cs`"
/// arrived on screen with its asterisks and backticks intact, which is the single most unfinished-
/// looking thing in the builder: the model is formatting its answer and the window is refusing to.</para>
///
/// <para><b>A deliberately small subset</b>, and the smallness is the point. Headings, bold, italic,
/// inline code and bullets are what a model writing about code produces; tables, links, images and
/// nested blockquotes are not, and every construct added here is another way for a parser running on
/// untrusted model output to get something wrong. Anything unrecognised is shown verbatim, which is
/// exactly today's behaviour — so this can only improve a reply, never mangle one.</para>
///
/// <para>Code FENCES are not handled here on purpose: the transcript strips them to a marker upstream,
/// because the code itself belongs in the workbench where it can be edited, not in the chat where it
/// can only be scrolled past.</para>
/// </summary>
public static class MarkdownText
{
    /// <summary>Set this instead of <c>Text</c> and the block renders formatted.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(MarkdownText),
        new PropertyMetadata(string.Empty, OnSourceChanged));

    public static string GetSource(DependencyObject element) =>
        (string)element.GetValue(SourceProperty);

    public static void SetSource(DependencyObject element, string value) =>
        element.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Inlines.Clear();

        var text = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Render(block, text);
        }
        catch (Exception)
        {
            // NEVER let a formatting failure cost the message. This parses model output, which is the
            // least trustworthy input in the system, and a reply the user cannot read is a far worse
            // outcome than one rendered without its bold.
            block.Inlines.Clear();
            block.Inlines.Add(new Run(text));
        }
    }

    /// <summary>Headings are drawn at this multiple of the block's own size, so the scale follows the
    /// theme rather than hard-coding a point size that only suits one density.</summary>
    private const double HeadingScale = 1.08;

    private static void Render(TextBlock block, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var first = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // A blank line is a paragraph break: one empty line's worth of space, not two. Models emit
            // runs of them, and reproducing each would leave a reply floating in its own gaps.
            if (line.Length == 0)
            {
                if (!first && block.Inlines.LastInline is not LineBreak) block.Inlines.Add(new LineBreak());
                continue;
            }

            if (!first) block.Inlines.Add(new LineBreak());
            first = false;

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            // ── heading ──────────────────────────────────────────────────────────────────────────
            if (trimmed.StartsWith('#'))
            {
                var hashes = 0;
                while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;

                if (hashes <= 4 && hashes < trimmed.Length && trimmed[hashes] == ' ')
                {
                    var span = new Span { FontWeight = FontWeights.SemiBold, FontSize = block.FontSize * HeadingScale };
                    AppendInline(span.Inlines, trimmed[(hashes + 1)..].Trim());
                    block.Inlines.Add(span);
                    continue;
                }
            }

            // ── bullet ───────────────────────────────────────────────────────────────────────────
            if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '•' && trimmed[1] == ' ')
            {
                // Spaces rather than a real hanging indent: a TextBlock has no list layout, and the
                // alternative is a nested control per bullet, which costs more than it buys for the
                // three-item lists this actually sees.
                block.Inlines.Add(new Run(new string(' ', Math.Min(indent, 8)) + "•  "));
                AppendInline(block.Inlines, trimmed[2..].Trim());
                continue;
            }

            AppendInline(block.Inlines, line);
        }
    }

    /// <summary>
    /// Bold, italic and inline code within one line.
    ///
    /// <para>Hand-written rather than regex, because the interesting cases are the malformed ones — an
    /// unclosed <c>**</c>, a lone backtick in prose — and the rule for those is "show it verbatim",
    /// which a scanner expresses directly and a pattern expresses by accident.</para>
    /// </summary>
    private static void AppendInline(InlineCollection target, string line)
    {
        var literal = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            // `code`
            if (line[i] == '`' && Closing(line, i + 1, "`") is { } codeEnd)
            {
                Flush(target, literal);
                target.Add(new Run(line[(i + 1)..codeEnd])
                {
                    FontFamily = Mono,
                    Background = CodeGround,
                });
                i = codeEnd;
                continue;
            }

            // **bold**
            if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '*'
                && Closing(line, i + 2, "**") is { } boldEnd)
            {
                Flush(target, literal);
                var span = new Span { FontWeight = FontWeights.SemiBold };
                AppendInline(span.Inlines, line[(i + 2)..boldEnd]);
                target.Add(span);
                i = boldEnd + 1;
                continue;
            }

            // *italic* and _italic_. Only when the delimiter is not touching a word on the wrong side,
            // so snake_case_identifiers and multiplication survive intact.
            if ((line[i] == '*' || line[i] == '_')
                && (i == 0 || !char.IsLetterOrDigit(line[i - 1]))
                && Closing(line, i + 1, line[i].ToString()) is { } italicEnd
                && italicEnd > i + 1)
            {
                Flush(target, literal);
                var span = new Span { FontStyle = FontStyles.Italic };
                AppendInline(span.Inlines, line[(i + 1)..italicEnd]);
                target.Add(span);
                i = italicEnd;
                continue;
            }

            literal.Append(line[i]);
        }

        Flush(target, literal);
    }

    /// <summary>Where the closing delimiter is, or null — in which case the opener is just a character.</summary>
    private static int? Closing(string line, int from, string delimiter)
    {
        if (from >= line.Length) return null;

        var at = line.IndexOf(delimiter, from, StringComparison.Ordinal);
        return at < 0 ? null : at;
    }

    private static void Flush(InlineCollection target, StringBuilder literal)
    {
        if (literal.Length == 0) return;

        target.Add(new Run(literal.ToString()));
        literal.Clear();
    }

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>A wash behind inline code rather than a border: at 13px a border round three words is
    /// noise, and the point is only to say "this is an identifier, not prose".</summary>
    private static readonly Brush CodeGround = Frozen(Color.FromArgb(0x2E, 0x7F, 0x8B, 0xA6));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
