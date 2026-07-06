using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NoPdf.Core.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace NoPdf.Core.Annotations;

/// <summary>
/// Reads known markup annotations out of a PDF into editable models and removes
/// them from the document, returning cleaned bytes. This lets the app render and
/// edit them itself (no double-draw, no duplication when re-saved), while unknown
/// annotation types stay in the document and continue to render normally.
/// </summary>
public static class AnnotationReader
{
    private static readonly HashSet<string> Known = new()
    {
        "/Highlight", "/Square", "/Line", "/PolyLine", "/Polygon", "/FreeText", "/Text",
    };

    public static (byte[] cleaned, List<PdfAnnotationModel> models) LoadAndStrip(byte[] source)
    {
        var models = new List<PdfAnnotationModel>();
        using var input = new MemoryStream(source);
        PdfDocument doc;
        try { doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify); }
        catch { return (source, models); } // unreadable for editing — leave as-is

        using (doc)
        {
            bool changed = false;
            for (int pi = 0; pi < doc.PageCount; pi++)
            {
                var page = doc.Pages[pi];
                var annots = page.Elements.GetArray("/Annots");
                if (annots is null) continue;

                var keep = new PdfArray(doc);
                foreach (var item in annots.Elements)
                {
                    var dict = Resolve(item);
                    string? subtype = dict?.Elements.GetName("/Subtype");
                    if (dict is not null && subtype is not null && Known.Contains(subtype))
                    {
                        var model = Parse(dict, subtype, pi);
                        if (model is not null) { models.Add(model); changed = true; continue; }
                    }
                    keep.Elements.Add(item);
                }
                if (keep.Elements.Count != annots.Elements.Count)
                    page.Elements["/Annots"] = keep;
            }

            if (!changed) return (source, models);

            using var output = new MemoryStream();
            doc.Save(output, closeStream: false);
            return (output.ToArray(), models);
        }
    }

    private static PdfAnnotationModel? Parse(PdfDictionary d, string subtype, int pageIndex)
    {
        var color = ReadColor(d, "/C") ?? AnnotColor.Red;
        double width = ReadBorderWidth(d);
        string? contents = d.Elements.ContainsKey("/Contents") ? d.Elements.GetString("/Contents") : null;

        switch (subtype)
        {
            case "/Highlight":
            {
                var quads = ReadQuads(d);
                if (quads.Count == 0) return null;
                return new HighlightAnnotation { PageIndex = pageIndex, Quads = quads, Color = color, Contents = contents };
            }
            case "/Square":
            {
                var rect = ReadRect(d, "/Rect");
                if (rect is null) return null;
                return new SquareAnnotation { PageIndex = pageIndex, Rect = rect.Value, Color = color, StrokeWidth = width, Interior = ReadColor(d, "/IC"), Contents = contents };
            }
            case "/Line":
            {
                var l = ReadReals(d, "/L");
                if (l.Count < 4) return null;
                bool arrow = HasArrow(d);
                return new LineAnnotation { PageIndex = pageIndex, Start = new PdfPoint(l[0], l[1]), End = new PdfPoint(l[2], l[3]), Arrow = arrow, Color = color, StrokeWidth = width, Contents = contents };
            }
            case "/PolyLine":
            case "/Polygon":
            {
                var pts = ReadPoints(d, "/Vertices");
                if (pts.Count < 2) return null;
                return new PolylineAnnotation { PageIndex = pageIndex, Points = pts, Closed = subtype == "/Polygon", Color = color, StrokeWidth = width, Contents = contents };
            }
            case "/FreeText":
            {
                var rect = ReadRect(d, "/Rect");
                if (rect is null) return null;
                double fontSize = ParseFontSize(d.Elements.ContainsKey("/DA") ? d.Elements.GetString("/DA") : null);
                bool callout = d.Elements.GetName("/IT") == "/FreeTextCallout" || d.Elements.ContainsKey("/CL");
                if (callout)
                {
                    var cl = ReadReals(d, "/CL");
                    var tip = cl.Count >= 2 ? new PdfPoint(cl[0], cl[1]) : new PdfPoint(rect.Value.Left, rect.Value.Bottom);
                    PdfPoint? knee = cl.Count >= 6 ? new PdfPoint(cl[2], cl[3]) : null;
                    return new CalloutAnnotation { PageIndex = pageIndex, Rect = rect.Value, Tip = tip, Knee = knee, FontSize = fontSize, Color = color, StrokeWidth = width, Contents = contents };
                }
                return new FreeTextAnnotation { PageIndex = pageIndex, Rect = rect.Value, FontSize = fontSize, Color = color, StrokeWidth = width, Contents = contents };
            }
            case "/Text":
            {
                var rect = ReadRect(d, "/Rect");
                if (rect is null) return null;
                return new StickyNoteAnnotation { PageIndex = pageIndex, Position = new PdfPoint(rect.Value.Left, rect.Value.Top), Color = color, Contents = contents };
            }
        }
        return null;
    }

    // ---------- field readers ----------

    private static PdfDictionary? Resolve(PdfItem item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    private static TextRect? ReadRect(PdfDictionary d, string key)
    {
        var a = ReadReals(d, key);
        if (a.Count < 4) return null;
        double l = Math.Min(a[0], a[2]), b = Math.Min(a[1], a[3]);
        double r = Math.Max(a[0], a[2]), t = Math.Max(a[1], a[3]);
        return new TextRect(l, b, r, t);
    }

    private static List<double> ReadReals(PdfDictionary d, string key)
    {
        var result = new List<double>();
        var arr = d.Elements.GetArray(key);
        if (arr is null) return result;
        foreach (var it in arr.Elements)
        {
            var v = it is PdfReference r ? r.Value : it;
            switch (v)
            {
                case PdfReal pr: result.Add(pr.Value); break;
                case PdfInteger pi: result.Add(pi.Value); break;
            }
        }
        return result;
    }

    private static List<PdfPoint> ReadPoints(PdfDictionary d, string key)
    {
        var flat = ReadReals(d, key);
        var pts = new List<PdfPoint>();
        for (int i = 0; i + 1 < flat.Count; i += 2) pts.Add(new PdfPoint(flat[i], flat[i + 1]));
        return pts;
    }

    private static List<TextRect> ReadQuads(PdfDictionary d)
    {
        var f = ReadReals(d, "/QuadPoints");
        var quads = new List<TextRect>();
        for (int i = 0; i + 7 < f.Count; i += 8)
        {
            double minX = Math.Min(Math.Min(f[i], f[i + 2]), Math.Min(f[i + 4], f[i + 6]));
            double maxX = Math.Max(Math.Max(f[i], f[i + 2]), Math.Max(f[i + 4], f[i + 6]));
            double minY = Math.Min(Math.Min(f[i + 1], f[i + 3]), Math.Min(f[i + 5], f[i + 7]));
            double maxY = Math.Max(Math.Max(f[i + 1], f[i + 3]), Math.Max(f[i + 5], f[i + 7]));
            quads.Add(new TextRect(minX, minY, maxX, maxY));
        }
        return quads;
    }

    private static AnnotColor? ReadColor(PdfDictionary d, string key)
    {
        var c = ReadReals(d, key);
        if (c.Count < 3) return null;
        return new AnnotColor(To255(c[0]), To255(c[1]), To255(c[2]));
    }

    private static double ReadBorderWidth(PdfDictionary d)
    {
        var bs = d.Elements.GetDictionary("/BS");
        if (bs is not null && bs.Elements.ContainsKey("/W"))
            return bs.Elements.GetReal("/W");
        var border = d.Elements.GetArray("/Border");
        if (border is not null && border.Elements.Count >= 3)
            return border.Elements.GetReal(2);
        return 2.0;
    }

    private static bool HasArrow(PdfDictionary d)
    {
        var le = d.Elements.GetArray("/LE");
        if (le is null) return false;
        foreach (var it in le.Elements)
        {
            string? n = (it as PdfName)?.Value;
            if (n is not null && n.Contains("Arrow")) return true;
        }
        return false;
    }

    private static double ParseFontSize(string? da)
    {
        if (string.IsNullOrEmpty(da)) return 12;
        var toks = da.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < toks.Length; i++)
            if (toks[i] == "Tf" && double.TryParse(toks[i - 1], NumberStyles.Any, CultureInfo.InvariantCulture, out double v) && v > 0)
                return v;
        return 12;
    }

    private static byte To255(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
