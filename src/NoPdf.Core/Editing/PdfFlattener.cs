using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace NoPdf.Core.Editing;

/// <summary>
/// Bakes annotations and form fields into page content, so what was an editable overlay
/// becomes part of the page itself.
///
/// Every annotation already carries a normal appearance stream (/AP /N), which is a form
/// XObject — flattening draws that XObject into the page's content stream at the annotation's
/// rectangle and then removes the annotation. Nothing is re-rendered, so a flattened page looks
/// exactly like the unflattened one.
/// </summary>
public static class PdfFlattener
{
    /// <summary>Annotation flags (PDF 32000 12.5.3).</summary>
    private const int FlagHidden = 1 << 1;
    private const int FlagNoView = 1 << 5;

    /// <summary>How many annotations were drawn into the page content.</summary>
    public static int LastFlattenedCount { get; private set; }

    /// <summary>
    /// Returns the document with its annotations and form fields drawn into the pages.
    /// </summary>
    public static byte[] Flatten(byte[] source)
    {
        using var input = new MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        int flattened = 0;
        for (int i = 0; i < doc.PageCount; i++)
            flattened += FlattenPage(doc, doc.Pages[i]);

        // The fields' widgets have been drawn in and removed, so the form itself has to go too
        // — leaving it behind would give a viewer a form whose fields have no widgets, and some
        // viewers respond by re-creating them.
        doc.Internals.Catalog.Elements.Remove("/AcroForm");

        LastFlattenedCount = flattened;
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    /// <summary>True if the document has anything that flattening would change.</summary>
    public static bool HasFlattenable(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            for (int i = 0; i < doc.PageCount; i++)
            {
                var annots = doc.Pages[i].Elements.GetArray("/Annots");
                for (int j = 0; j < (annots?.Elements.Count ?? 0); j++)
                    if (Resolve(annots!.Elements[j]) is PdfDictionary a && IsFlattenable(a)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static int FlattenPage(PdfDocument doc, PdfPage page)
    {
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null || annots.Elements.Count == 0) return 0;

        var content = new StringBuilder();
        var drawn = new List<PdfItem>();
        int n = 0;

        for (int i = 0; i < annots.Elements.Count; i++)
        {
            var item = annots.Elements[i];
            if (Resolve(item) is not PdfDictionary annot) continue;
            if (!IsFlattenable(annot)) { continue; }

            var form = NormalAppearance(annot);
            if (form is null) continue;                 // nothing to draw — leave it alone

            var rect = annot.Elements.GetRectangle("/Rect");
            if (rect.Width <= 0 || rect.Height <= 0) { drawn.Add(item); continue; }

            string name = AddXObject(doc, page, form);
            content.Append(PlaceForm(form, rect, name));
            drawn.Add(item);
            n++;
        }

        if (n > 0)
        {
            AppendContent(doc, page, content.ToString());
            foreach (var item in drawn) annots.Elements.Remove(item);
            if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
        }
        return n;
    }

    /// <summary>
    /// Popups are the note windows a viewer opens on demand, never part of the page; hidden and
    /// no-view annotations are deliberately not shown. Drawing any of them would make content
    /// appear that the reader never saw.
    /// </summary>
    private static bool IsFlattenable(PdfDictionary annot)
    {
        if (annot.Elements.GetName("/Subtype") == "/Popup") return false;
        int flags = annot.Elements.GetInteger("/F");
        return (flags & (FlagHidden | FlagNoView)) == 0;
    }

    /// <summary>
    /// The appearance to draw. /AP /N is either the form XObject itself or, for anything with
    /// states (a checkbox, a radio button), a dictionary of them selected by /AS.
    /// </summary>
    private static PdfDictionary? NormalAppearance(PdfDictionary annot)
    {
        if (Resolve(annot.Elements["/AP"]) is not PdfDictionary ap) return null;
        if (Resolve(ap.Elements["/N"]) is not PdfDictionary normal) return null;

        // A form XObject has a /BBox; a state dictionary does not.
        if (normal.Elements.ContainsKey("/BBox")) return normal;

        string? state = annot.Elements.GetName("/AS");
        if (!string.IsNullOrEmpty(state) && Resolve(normal.Elements[state]) is PdfDictionary chosen)
            return chosen.Elements.ContainsKey("/BBox") ? chosen : null;

        // No /AS to pick with: only safe if there is exactly one state.
        if (normal.Elements.Count == 1)
        {
            foreach (var key in normal.Elements.Keys)
                if (Resolve(normal.Elements[key]) is PdfDictionary only && only.Elements.ContainsKey("/BBox"))
                    return only;
        }
        return null;
    }

    /// <summary>Registers the appearance in the page's resources and returns its name.</summary>
    private static string AddXObject(PdfDocument doc, PdfPage page, PdfDictionary form)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(doc);
            page.Elements["/Resources"] = resources;
        }

        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
        {
            xobjects = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
        }

        // A name that cannot collide with one the page already uses.
        string name;
        int i = 0;
        do { name = "/NoPdfFlat" + i++; } while (xobjects.Elements.ContainsKey(name));

        if (form.Reference is null) doc.Internals.AddObject(form);
        xobjects.Elements[name] = form.Reference ?? (PdfItem)form;
        return name;
    }

    /// <summary>
    /// The content that draws the appearance at the annotation's rectangle. Per PDF 32000
    /// 12.5.5: transform the form's BBox by its Matrix, then map that box onto Rect. For
    /// appearances noPDF writes the BBox already is the rectangle and this comes out as the
    /// identity, but imported annotations and form widgets commonly use a BBox at the origin
    /// plus a Matrix, and those have to be placed properly.
    /// </summary>
    private static string PlaceForm(PdfDictionary form, PdfRectangle rect, string name)
    {
        var bbox = form.Elements.GetRectangle("/BBox");
        double[] m = { 1, 0, 0, 1, 0, 0 };
        var matrix = form.Elements.GetArray("/Matrix");
        if (matrix is not null && matrix.Elements.Count == 6)
            for (int i = 0; i < 6; i++) m[i] = matrix.Elements.GetReal(i);

        // The BBox corners after the form matrix.
        double[] xs = new double[4], ys = new double[4];
        double[] cx = { bbox.X1, bbox.X2, bbox.X1, bbox.X2 };
        double[] cy = { bbox.Y1, bbox.Y1, bbox.Y2, bbox.Y2 };
        for (int i = 0; i < 4; i++)
        {
            xs[i] = m[0] * cx[i] + m[2] * cy[i] + m[4];
            ys[i] = m[1] * cx[i] + m[3] * cy[i] + m[5];
        }
        double minX = Math.Min(Math.Min(xs[0], xs[1]), Math.Min(xs[2], xs[3]));
        double maxX = Math.Max(Math.Max(xs[0], xs[1]), Math.Max(xs[2], xs[3]));
        double minY = Math.Min(Math.Min(ys[0], ys[1]), Math.Min(ys[2], ys[3]));
        double maxY = Math.Max(Math.Max(ys[0], ys[1]), Math.Max(ys[2], ys[3]));

        // A degenerate box can't be scaled onto the rectangle; draw it untransformed.
        double sx = maxX - minX > 1e-6 ? rect.Width / (maxX - minX) : 1;
        double sy = maxY - minY > 1e-6 ? rect.Height / (maxY - minY) : 1;
        double tx = rect.X1 - minX * sx;
        double ty = rect.Y1 - minY * sy;

        return "q " + F(sx) + " 0 0 " + F(sy) + " " + F(tx) + " " + F(ty) + " cm " + name + " Do Q\n";
    }

    /// <summary>
    /// Adds the drawing to the end of the page. The existing content is wrapped in q/Q first:
    /// a page's content is not required to leave the graphics state as it found it, and without
    /// the wrapper a stray transform in it would displace everything drawn here.
    /// </summary>
    private static void AppendContent(PdfDocument doc, PdfPage page, string content)
    {
        var before = new PdfDictionary(doc);
        doc.Internals.AddObject(before);
        before.CreateStream(Encoding.ASCII.GetBytes("q\n"));

        var after = new PdfDictionary(doc);
        doc.Internals.AddObject(after);
        after.CreateStream(Encoding.ASCII.GetBytes("Q\n" + content));

        var existing = page.Elements["/Contents"];
        var array = new PdfArray(doc);
        array.Elements.Add(before.Reference!);
        if (Resolve(existing) is PdfArray old)
            for (int i = 0; i < old.Elements.Count; i++) array.Elements.Add(old.Elements[i]);
        else if (existing is not null)
            array.Elements.Add(existing);
        array.Elements.Add(after.Reference!);
        page.Elements["/Contents"] = array;
    }

    private static PdfObject? Resolve(PdfItem? item) => item switch
    {
        PdfReference r => r.Value,
        PdfObject o => o,
        _ => null,
    };

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
