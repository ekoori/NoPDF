using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NoPdf.Core.Rendering;

namespace NoPdf.App.Printing;

/// <summary>What to print and how — defaults come from the config, the print dialog can override.</summary>
public sealed class PrintOptions
{
    /// <summary>Printer name; blank = the system default.</summary>
    public string Printer { get; set; } = "";
    public int Copies { get; set; } = 1;
    /// <summary>Zero-based page indices to print; null/empty = all.</summary>
    public IReadOnlyList<int>? Pages { get; set; }
    /// <summary>Scale each page to fill the paper (keeping aspect) rather than 1:1.</summary>
    public bool FitToPage { get; set; } = true;
    public bool Grayscale { get; set; }
    public bool Landscape { get; set; }
    /// <summary>Print to this file instead of paper (e.g. with "Microsoft Print to PDF").</summary>
    public string? OutputFile { get; set; }
}

/// <summary>
/// Prints a PDF by rasterising its pages with PDFium and sending them to a printer.
/// Windows-only: .NET's printing stack (System.Drawing.Printing) doesn't exist elsewhere.
/// </summary>
public static class PrintService
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Printers()
    {
        var list = new List<string>();
        try
        {
            foreach (string p in System.Drawing.Printing.PrinterSettings.InstalledPrinters) list.Add(p);
        }
        catch { }
        return list;
    }

    [SupportedOSPlatform("windows")]
    public static string DefaultPrinter()
    {
        try { return new System.Drawing.Printing.PrinterSettings().PrinterName; }
        catch { return ""; }
    }

    /// <summary>Prints the document. Throws on failure so the caller can report it.</summary>
    [SupportedOSPlatform("windows")]
    public static void Print(byte[] pdfBytes, PrintOptions opts)
    {
        using var doc = PdfDocument.OpenBytes(pdfBytes);
        var pages = new List<int>();
        if (opts.Pages is { Count: > 0 }) pages.AddRange(opts.Pages);
        else for (int i = 0; i < doc.PageCount; i++) pages.Add(i);
        if (pages.Count == 0) throw new InvalidOperationException("No pages to print.");

        using var pd = new System.Drawing.Printing.PrintDocument();
        if (!string.IsNullOrWhiteSpace(opts.Printer)) pd.PrinterSettings.PrinterName = opts.Printer;
        if (!pd.PrinterSettings.IsValid) throw new InvalidOperationException($"Printer not available: {opts.Printer}");
        pd.PrinterSettings.Copies = (short)Math.Clamp(opts.Copies, 1, 99);
        if (!string.IsNullOrWhiteSpace(opts.OutputFile))
        {
            pd.PrinterSettings.PrintToFile = true;
            pd.PrinterSettings.PrintFileName = opts.OutputFile;
        }
        pd.DefaultPageSettings.Landscape = opts.Landscape;
        pd.DefaultPageSettings.Color = !opts.Grayscale;
        pd.DocumentName = "noPDF";

        int index = 0;
        const double renderDpi = 150; // quality/size balance for rasterised pages
        pd.PrintPage += (_, e) =>
        {
            var bounds = e.PageSettings.PrintableArea;              // 1/100 inch
            var size = doc.GetPageSize(pages[index]);               // points
            double pageWIn = size.Width / 72.0, pageHIn = size.Height / 72.0;

            // Work out the drawn size in inches, then rasterise to match at renderDpi.
            double drawWIn = pageWIn, drawHIn = pageHIn;
            if (opts.FitToPage)
            {
                double s = Math.Min(bounds.Width / 100.0 / pageWIn, bounds.Height / 100.0 / pageHIn);
                drawWIn = pageWIn * s; drawHIn = pageHIn * s;
            }
            double renderScale = drawWIn * renderDpi / size.Width;  // pixels per point

            var rendered = doc.RenderPage(pages[index], renderScale);
            using (var bmp = ToBitmap(rendered))
            {
                float w = (float)(drawWIn * 100), h = (float)(drawHIn * 100);
                float x = bounds.X + Math.Max(0, (bounds.Width - w) / 2);
                float y = bounds.Y + Math.Max(0, (bounds.Height - h) / 2);
                e.Graphics!.DrawImage(bmp, new System.Drawing.RectangleF(x, y, w, h));
            }
            index++;
            e.HasMorePages = index < pages.Count;
        };
        pd.Print();
    }

    /// <summary>Copies a PDFium BGRA buffer into a GDI+ bitmap.</summary>
    [SupportedOSPlatform("windows")]
    private static System.Drawing.Bitmap ToBitmap(RenderedPage page)
    {
        var bmp = new System.Drawing.Bitmap(page.Width, page.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, page.Width, page.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < page.Height; y++)
                Marshal.Copy(page.Pixels, y * page.Stride, data.Scan0 + y * data.Stride, page.Stride);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
