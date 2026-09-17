using System.IO;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The terminal accepts strategies and visualizers that draw themselves: a Blocks unit with its own
/// HTML/CSS page. Registration, installation and the authored-units loader all ask this one rule.
///
/// <para><b>Why a rule rather than a preference.</b> A unit drawn through the terminal's own controls
/// looks like every other one and is only as good as those controls, and the widget SDK's units never
/// got past that. A page is the author's — which is what Hyperion builds now — while the settings, book
/// and log around it stay the terminal's. Owner decision, 2026-09-15.</para>
///
/// <para><b>Decided from what is on disk, never from a claim.</b> The widget SDK is recognised by the
/// compiled assembly not referencing <c>DaxAlgo.Blocks</c>, read as metadata; the page by the entry file
/// actually being there and not empty.</para>
/// </summary>
public static class UnitPageRule
{
    /// <summary>The page's entry file, relative to the unit.</summary>
    public const string Entry = BlocksPackage.PageFolder + "/index.html";

    private const string Accepted =
        "Only strategies and visualizers with their own HTML/CSS page are accepted: a unit written against the "
        + "Blocks SDK with a " + Entry + ".";

    /// <summary>Why a set of authored files cannot be registered, or null when it can.</summary>
    /// <param name="files">The unit's files, page files named under <c>ui/</c>.</param>
    /// <param name="isBlocksUnit">Whether the files compiled as a Blocks unit.</param>
    public static string? RefuseFiles(IEnumerable<StrategyFile> files, bool isBlocksUnit)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (!isBlocksUnit)
            return "This unit draws through the terminal's controls (the widget SDK). " + Accepted;

        return files.Any(f => string.Equals(f.Name.Replace('\\', '/'), Entry, StringComparison.OrdinalIgnoreCase)
                              && !string.IsNullOrWhiteSpace(f.Content))
            ? null
            : $"This unit has no page — {Entry} is missing or empty. " + Accepted;
    }

    /// <summary>
    /// Why a package cannot be accepted, read from its manifest alone, or null when it can.
    ///
    /// <para>Asked before a single payload is written: a package with nothing to draw is refused while it
    /// is still a file, and the Plugin Manager can say which half is missing rather than reporting a
    /// staging folder that was cleaned up behind it.</para>
    /// </summary>
    /// <param name="payloads">The manifest's payload paths, as declared (<c>payload/…</c>).</param>
    public static string? RefusePayloads(IEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var wanted = "payload/" + Entry;
        return payloads.Any(p => string.Equals(p?.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"This package carries no page — {Entry} is not in it. " + Accepted;
    }

    /// <summary>Why an installed or staged unit folder cannot be accepted, or null when it can.</summary>
    /// <param name="folder">The unit's folder.</param>
    /// <param name="mainAssembly">Its entry assembly.</param>
    public static string? RefuseFolder(string folder, string mainAssembly)
    {
        if (!BlocksPackage.IsBlocksAssembly(mainAssembly))
            return $"{Path.GetFileNameWithoutExtension(mainAssembly)} draws through the terminal's controls (the widget SDK). " + Accepted;

        var page = new FileInfo(Path.Combine(folder, BlocksPackage.PageFolder, "index.html"));
        return page.Exists && page.Length > 0
            ? null
            : $"{Path.GetFileNameWithoutExtension(mainAssembly)} has no page — {Entry} is missing or empty. " + Accepted;
    }
}
