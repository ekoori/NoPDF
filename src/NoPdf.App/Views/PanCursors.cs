using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NoPdf.App.Views;

/// <summary>Open- and grabbing-hand cursors for the pan/view tool, rendered from
/// emoji glyphs (Avalonia has no standard closed-hand cursor).</summary>
public static class PanCursors
{
    private static Cursor? _open, _grab;

    public static Cursor Open => _open ??= Build("\U0001F590"); // raised hand with fingers splayed
    public static Cursor Grab => _grab ??= Build("✊");     // raised fist

    private static Cursor Build(string glyph)
    {
        try
        {
            const int size = 32;
            var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"), 22, Brushes.Black);
            var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (var dc = rtb.CreateDrawingContext())
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            return new Cursor(rtb, new PixelPoint(size / 2, size / 2));
        }
        catch
        {
            return new Cursor(StandardCursorType.Hand);
        }
    }
}
