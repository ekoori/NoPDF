using System;
using System.Globalization;

namespace NoPdf.Core.Editing;

/// <summary>
/// Parses the page-size names used by <c>:newpage</c> and the <c>default_page_size</c> config
/// setting. Named sizes take an <c>l</c> suffix for landscape (<c>a4l</c>); anything else is
/// read as millimetres, <c>WxH</c>.
/// </summary>
public static class PageSizes
{
    private const double MmToPt = 72.0 / 25.4;

    /// <summary>Named sizes in millimetres, portrait.</summary>
    private static readonly (string name, double w, double h)[] Named =
    {
        ("a3", 297, 420),
        ("a4", 210, 297),
        ("a5", 148, 210),
        ("letter", 215.9, 279.4),   // 8.5 x 11 in
        ("legal", 215.9, 355.6),    // 8.5 x 14 in
    };

    /// <summary>Every size name that can be given, for help text and error messages.</summary>
    public static string NameList => "a3, a4, a5, letter, legal (add 'l' for landscape), or WxH in mm";

    /// <summary>
    /// Parses a size to points. Returns false with a usable message rather than throwing,
    /// since this is driven straight from what the user typed.
    /// </summary>
    public static bool TryParse(string? spec, out double widthPt, out double heightPt, out string? error)
    {
        widthPt = heightPt = 0;
        error = null;

        spec = spec?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(spec)) { error = "No page size given"; return false; }

        // Named, optionally with a landscape suffix. Checked longest-first so "legal" is not
        // mistaken for "lega" + "l".
        foreach (var (name, w, h) in Named)
        {
            if (spec == name) { widthPt = w * MmToPt; heightPt = h * MmToPt; return true; }
            if (spec == name + "l") { widthPt = h * MmToPt; heightPt = w * MmToPt; return true; }
        }

        // WxH in millimetres.
        int x = spec.IndexOf('x');
        if (x > 0 && x < spec.Length - 1
            && double.TryParse(spec[..x].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mmW)
            && double.TryParse(spec[(x + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mmH))
        {
            // A page has to be big enough to be a page, and PDF tops out at 200 inches.
            if (mmW < 10 || mmH < 10 || mmW > 5080 || mmH > 5080)
            {
                error = "Page size must be between 10mm and 5080mm";
                return false;
            }
            widthPt = mmW * MmToPt;
            heightPt = mmH * MmToPt;
            return true;
        }

        error = $"Unknown page size '{spec}' — use {NameList}";
        return false;
    }

    /// <summary>Points for a named size, falling back to A4 for an unusable one. For config
    /// values, where a typo shouldn't stop the app from giving you a page.</summary>
    public static (double widthPt, double heightPt) ParseOrA4(string? spec)
        => TryParse(spec, out double w, out double h, out _) ? (w, h) : (210 * MmToPt, 297 * MmToPt);
}
