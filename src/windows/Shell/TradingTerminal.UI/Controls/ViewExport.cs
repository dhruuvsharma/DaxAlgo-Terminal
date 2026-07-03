using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// PNG snapshot export for tool windows — renders any <see cref="FrameworkElement"/> (typically a
/// window's content root or a chart host) to a PNG at its on-screen size and DPI, behind a save
/// dialog. View-side by design: the visual tree is a view concern, so windows call this from a
/// toolbar button's click handler; data exports (CSV) stay VM-side via <c>UiFile</c>.
/// </summary>
public static class ViewExport
{
    /// <summary>Renders <paramref name="element"/> to a PNG chosen via a save dialog. Returns the
    /// saved path, or null when the element has no size yet or the user cancelled.</summary>
    public static string? SavePng(FrameworkElement element, string suggestedName)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return null;

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = suggestedName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? suggestedName
                : suggestedName + ".png",
        };
        if (dialog.ShowDialog() != true) return null;

        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Render via a VisualBrush-backed DrawingVisual so the element's layout offset within its
        // parent doesn't shift (or blank) the capture — the classic RenderTargetBitmap gotcha.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(
                new VisualBrush(element),
                null,
                new Rect(new Size(element.ActualWidth, element.ActualHeight)));
        }
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
        return dialog.FileName;
    }
}
