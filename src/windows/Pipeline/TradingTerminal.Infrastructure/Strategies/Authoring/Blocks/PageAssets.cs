using System.Text.Json;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// What a unit's page loads from its own folder that the unit does not have.
///
/// <para><b>The gate passed a page that could not work.</b> Measured 2026-09-19 on NVIDIA NIM's GLM 5.3
/// Flash: <c>ui/index.html</c> loaded <c>style.css</c> and <c>app.js</c>, neither file existed, and the
/// page still called <c>dax.ready()</c> from an inline script and was not a flat colour — so every rung
/// cleared and the user opened a battlefield that was an unstyled list of labels. The probe judges what
/// happens when the page runs; this judges what the page asks for, which a model can fix by name.</para>
///
/// <para>Local means relative to the page: a CDN URL, a data URL, an anchor, and a bare module name the
/// page's import map resolves (<c>three</c>, <c>three/addons/…</c>) are not the unit's to supply.</para>
/// </summary>
public static partial class PageAssets
{
    /// <summary>One missing file: who asked for it, and the path it would have to have.</summary>
    public sealed record MissingAsset(string From, string Reference, string Path);

    [GeneratedRegex("""<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex("""<link\b[^>]*\bhref\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHref();

    [GeneratedRegex("""(?:\bimport\s*(?:[\w*{}\s,]+\s*from\s*)?|\bexport\s*[\w*{}\s,]+\s*from\s*|\bimport\s*\(\s*)["']([^"'\s]+)["']""")]
    private static partial Regex ModuleSpecifier();

    [GeneratedRegex("""@import\s+(?:url\(\s*)?["']?([^"')\s;]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex("""<script\b[^>]*type\s*=\s*["']importmap["'][^>]*>(.*?)</script>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImportMap();

    [GeneratedRegex("""<script\b(?![^>]*\bsrc\s*=)[^>]*>(.*?)</script>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineScript();

    /// <summary>Every local file the page files refer to that is not among them.</summary>
    /// <param name="pageFiles">The unit's page files, named as the compiler keeps them (<c>ui/…</c>).</param>
    public static IReadOnlyList<MissingAsset> Missing(IReadOnlyList<StrategyFile> pageFiles)
    {
        ArgumentNullException.ThrowIfNull(pageFiles);

        var have = new HashSet<string>(pageFiles.Select(f => Normalise(f.Name)), StringComparer.OrdinalIgnoreCase);
        var mapped = MappedNames(pageFiles);
        var missing = new List<MissingAsset>();

        foreach (var file in pageFiles)
        {
            var name = Normalise(file.Name);
            foreach (var reference in References(name, file.Content ?? string.Empty))
            {
                if (!IsLocal(reference, mapped, IsHtml(name))) continue;

                var path = Resolve(name, reference);
                if (path is null || have.Contains(path)) continue;
                if (missing.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                missing.Add(new MissingAsset(name, reference, path));
            }
        }

        return missing;
    }

    private static IEnumerable<string> References(string name, string content)
    {
        if (IsHtml(name))
        {
            foreach (Match m in ScriptSrc().Matches(content)) yield return m.Groups[1].Value;
            foreach (Match m in LinkHref().Matches(content)) yield return m.Groups[1].Value;

            // Module imports written inline, which load files as surely as a src does.
            foreach (Match script in InlineScript().Matches(content))
                foreach (Match m in ModuleSpecifier().Matches(script.Groups[1].Value))
                    yield return m.Groups[1].Value;
        }
        else if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in ModuleSpecifier().Matches(StripComments(content))) yield return m.Groups[1].Value;
        }
        else if (name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match m in CssImport().Matches(content)) yield return m.Groups[1].Value;
        }
    }

    /// <summary>
    /// Whether a reference is to a file the unit must ship. From HTML every relative URL is; from a module
    /// only a specifier that starts like a path is — a bare name is the import map's, or a mistake the
    /// browser reports on its own.
    /// </summary>
    private static bool IsLocal(string reference, IReadOnlySet<string> mapped, bool fromHtml)
    {
        var r = reference.Trim();
        if (r.Length == 0 || r.StartsWith('#')) return false;
        if (r.StartsWith("//", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(r, "^[a-zA-Z][a-zA-Z0-9+.-]*:")) return false;    // https:, data:, blob:, mailto: …
        if (mapped.Any(key => key.EndsWith('/') ? r.StartsWith(key, StringComparison.Ordinal) : r == key)) return false;

        if (fromHtml) return true;
        return r.StartsWith("./", StringComparison.Ordinal) || r.StartsWith("../", StringComparison.Ordinal) || r.StartsWith('/');
    }

    /// <summary>The page path a reference points at, or null when it climbs out of <c>ui/</c>.</summary>
    private static string? Resolve(string from, string reference)
    {
        var clean = reference.Split('?', '#')[0].Replace('\\', '/');
        var folder = from.Contains('/') ? from[..from.LastIndexOf('/')] : "ui";

        var segments = new List<string>();
        string[] start = clean.StartsWith('/') ? ["ui"] : folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        segments.AddRange(start);

        foreach (var part in clean.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count <= 1) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return segments.Count >= 2 ? string.Join('/', segments) : null;
    }

    /// <summary>The names the pages' import maps resolve — never the unit's to supply.</summary>
    private static IReadOnlySet<string> MappedNames(IReadOnlyList<StrategyFile> pageFiles)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in pageFiles.Where(f => IsHtml(Normalise(f.Name))))
        {
            foreach (Match map in ImportMap().Matches(file.Content ?? string.Empty))
            {
                try
                {
                    using var json = JsonDocument.Parse(map.Groups[1].Value);
                    if (json.RootElement.TryGetProperty("imports", out var imports) && imports.ValueKind == JsonValueKind.Object)
                        foreach (var entry in imports.EnumerateObject())
                            names.Add(entry.Name);
                }
                catch (JsonException)
                {
                    // A broken import map is the probe's to report; it maps nothing here.
                }
            }
        }

        return names;
    }

    private static string StripComments(string js) =>
        Regex.Replace(js, @"/\*.*?\*/|(?<![:""'])//[^\n]*", string.Empty, RegexOptions.Singleline);

    private static string Normalise(string name) => name.Replace('\\', '/').Trim();

    private static bool IsHtml(string name) =>
        name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
}
