using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

/// <summary>
/// What a critic is shown: the artifact, and nothing about how it came to exist.
///
/// <para><b>The builder's reasoning is deliberately absent</b>, and that is the load-bearing rule of the
/// whole method. A critic handed the chain of decisions that produced a picture evaluates the decisions;
/// a critic handed the picture evaluates the picture. The second is the only one that catches a unit
/// whose reasoning was impeccable and whose axes are unlabelled.</para>
/// </summary>
/// <param name="Files">The source, so a maths or data-contract critic can read what it is judging.</param>
/// <param name="Raster">The render, when the host could produce one. Null on a headless host.</param>
/// <param name="Primitives">What the unit actually emitted when driven — the fallback for a critic that
/// cannot be shown a picture, and useful context even for one that can.</param>
/// <param name="Ladder">What the objective rungs already concluded. Given so a critic does not spend the
/// user's money re-deriving that something compiles.</param>
/// <param name="Layout">The panel arrangement.</param>
/// <param name="Kind">Strategy or visualizer, which changes what "good" means.</param>
public sealed record GauntletSubject(
    IReadOnlyList<StrategyFile> Files,
    UnitRaster? Raster,
    string Primitives,
    VerificationReport Ladder,
    UnitLayout? Layout,
    AuthoringKind Kind)
{
    /// <summary>
    /// A content hash of everything a critic sees.
    ///
    /// <para>This is what makes the gate skippable. Prime Agent's autonomous gate does not re-run a
    /// verification command when the workspace has not changed since the last attempt, and the same rule
    /// belongs here for a sharper reason: this harness has a measured history of spending a hundred and
    /// forty turns re-judging artifacts that had not moved. A critic pass over an identical subject
    /// returns an identical verdict and costs the user exactly as much as one that would not.</para>
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder();
            foreach (var file in Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                text.Append(file.Name).Append('').Append(file.Content).Append('');

            text.Append(Raster?.Hash ?? "no-raster").Append('').Append(Primitives);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}

/// <summary>
/// Turns a compiled unit into the thing critics judge.
/// </summary>
public static class GauntletSubjects
{
    /// <summary>How many drawn labels are quoted in the dump. Enough to tell whether the axes are
    /// labelled and the legend says anything; not so many that a busy heatmap fills the prompt.</summary>
    public const int MaximumQuotedTexts = 40;

    /// <summary>
    /// Builds the subject: drive the unit's draw once into a recorder, summarise it, and attach the
    /// raster if the host could make one.
    /// </summary>
    /// <param name="preview">The live, drawable instance the ladder already verified — the SAME one, so
    /// what a critic judges and what the pane shows cannot disagree.</param>
    public static async Task<GauntletSubject> BuildAsync(
        AuthoredUnitPreview preview,
        IReadOnlyList<StrategyFile> files,
        VerificationReport ladder,
        IUnitRasterizer? rasterizer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ladder);

        if (preview.Draw is not { } draw)
            return new GauntletSubject(
                files, null, "The unit draws nothing.", ladder, preview.Layout, preview.Kind);

        var recorder = new RecordingRenderSurface(now: DrawProbe.ProbeInstant);
        try
        {
            draw(recorder);
        }
        catch (Exception)
        {
            // A unit that throws mid-frame is the draw probe's finding, already on the ladder report
            // this subject carries. What it managed to emit first is still worth showing.
        }

        UnitRaster? raster = null;
        if (rasterizer is { CanRender: true })
            raster = await rasterizer.RenderAsync(draw, preview.Layout, ct: ct).ConfigureAwait(false);

        return new GauntletSubject(
            files, raster, Describe(recorder, preview.Layout), ladder, preview.Layout, preview.Kind);
    }

    /// <summary>
    /// The drawing commands, as a critic reads them.
    ///
    /// <para>Counts and labels rather than a call log. A critic asked whether the price axis is labelled
    /// needs the LABELS; it does not need six thousand Push coordinates, which would cost more than the
    /// picture and say less. The one exception is the axis calls, which carry their ranges — a y-axis
    /// running 0 to 1 on an instrument priced in the thousands is a real defect that no rung catches.</para>
    /// </summary>
    public static string Describe(RecordingRenderSurface recorder, UnitLayout? layout)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.AppendLine(string.Create(
            culture,
            $"Viewport {recorder.Viewport.Width:0}x{recorder.Viewport.Height:0}, "
            + $"{recorder.PrimitiveCount} primitive(s)."));

        if (recorder.IsBlank) text.AppendLine("NOTHING WAS DRAWN.");
        if (recorder.HasNonFiniteCoordinate) text.AppendLine("A COORDINATE WAS NOT FINITE.");

        if (layout?.Panels() is { Count: > 0 } declared)
            text.AppendLine($"Layout declares {declared.Count} panel(s): "
                            + string.Join(", ", declared.Select(p => p.Title)));

        if (recorder.Panels.Count > 0)
            text.AppendLine($"Panels drawn: {string.Join(", ", recorder.Panels)}");

        if (recorder.SeriesNames.Count > 0)
            text.AppendLine($"Series: {string.Join(", ", recorder.SeriesNames)}");

        foreach (var axis in recorder.Calls.Where(c => c.Kind is "AxisX" or "AxisY"))
            text.AppendLine(string.Create(
                culture, $"{axis.Kind} {axis.X:G6} to {axis.Y:G6}{(axis.Label is null ? "" : $" format '{axis.Label}'")}"));

        text.AppendLine(string.Create(
            culture,
            $"Counts: {recorder.Texts.Count} text, {recorder.Rectangles.Count} rect, "
            + $"{recorder.Lines.Count} line, {recorder.Markers.Count} marker, {recorder.Points.Count} point."));

        // Theme tokens versus literal colours. A unit painting in hard-coded hex looks right in one
        // theme and unreadable in the other, and the draw probe already refuses the extreme case —
        // saying it here lets a critic weigh the merely unwise ones.
        text.AppendLine(recorder.ThemeTokens.Count > 0
            ? $"Theme tokens used: {string.Join(", ", recorder.ThemeTokens.Distinct())}"
            : "NO THEME TOKENS USED — every colour in this frame is a literal.");

        if (recorder.Texts.Count > 0)
        {
            text.AppendLine("Labels drawn:");
            foreach (var label in recorder.Texts.Take(MaximumQuotedTexts))
                text.AppendLine(string.Create(culture, $"  ({label.X:0},{label.Y:0}) \"{label.Text}\""));

            if (recorder.Texts.Count > MaximumQuotedTexts)
                text.AppendLine($"  … and {recorder.Texts.Count - MaximumQuotedTexts} more");
        }

        return text.ToString();
    }
}
