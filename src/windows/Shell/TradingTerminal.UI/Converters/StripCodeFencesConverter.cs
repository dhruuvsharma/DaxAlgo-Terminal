using System.Globalization;
using System.Text;
using System.Windows.Data;

namespace TradingTerminal.UI.Converters;

/// <summary>
/// Replaces fenced code blocks (``` … ```) in an assistant reply with a one-line marker. The Vibe
/// Quant transcript is prose — the code itself is already extracted into the workbench editor, and
/// the per-turn file chips point at it, so repeating hundreds of monospace lines in the chat only
/// buries the conversation. An unterminated fence keeps its content (never silently drop text).
/// </summary>
public sealed class StripCodeFencesConverter : IValueConverter
{
    private const string Marker = "· · ·  code written to the workbench  · · ·";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || !text.Contains("```", StringComparison.Ordinal)) return value ?? string.Empty;

        var result = new StringBuilder(text.Length);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inFence = false;
        var fenceBuffer = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceBuffer.Clear();
                }
                else
                {
                    inFence = false;
                    result.AppendLine(Marker);
                }

                continue;
            }

            if (inFence) fenceBuffer.AppendLine(line);
            else result.AppendLine(line);
        }

        // A fence that never closed (mid-stream, or a sloppy reply): show it rather than lose it.
        if (inFence) result.Append(fenceBuffer);

        return result.ToString().TrimEnd();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
