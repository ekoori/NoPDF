using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NoPdf.Core.Rendering;

namespace NoPdf.App.Rendering;

public static class BitmapConverter
{
    /// <summary>Copies a PDFium BGRA render into an Avalonia <see cref="WriteableBitmap"/>.</summary>
    public static WriteableBitmap ToWriteableBitmap(RenderedPage page)
    {
        var wb = new WriteableBitmap(
            new PixelSize(page.Width, page.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var fb = wb.Lock();
        if (fb.RowBytes == page.Stride)
        {
            Marshal.Copy(page.Pixels, 0, fb.Address, page.Pixels.Length);
        }
        else
        {
            // Destination rows are padded differently; copy row by row.
            int copyBytes = Math.Min(fb.RowBytes, page.Stride);
            for (int y = 0; y < page.Height; y++)
            {
                Marshal.Copy(page.Pixels, y * page.Stride,
                    fb.Address + y * fb.RowBytes, copyBytes);
            }
        }
        return wb;
    }
}
