using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace NoPdf.App.Editing;

/// <summary>Reads a raster image off the system clipboard as PNG bytes.</summary>
public static class ClipboardImage
{
    public static async Task<(byte[] png, int width, int height)?> TryReadAsync(TopLevel? top)
    {
        var cb = top?.Clipboard;
        if (cb is null) return null;

        IAsyncDataTransfer? data;
        try { data = await cb.TryGetDataAsync(); }
        catch { return null; }
        if (data is null) return null;

        foreach (var item in data.Items)
        {
            object? raw;
            try { raw = await item.TryGetRawAsync(DataFormat.Bitmap); }
            catch { continue; }
            if (raw is Bitmap bmp)
            {
                try
                {
                    using var ms = new MemoryStream();
                    bmp.Save(ms); // PNG
                    return (ms.ToArray(), bmp.PixelSize.Width, bmp.PixelSize.Height);
                }
                catch { /* try next item */ }
            }
        }
        return null;
    }
}
