using System.Collections.Generic;
using System.IO;
using NoPdf.Core.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace NoPdf.Core.Annotations;

/// <summary>A clickable link on a page: a rectangle plus a destination (internal
/// page index or an external URI).</summary>
public sealed record PdfLink(TextRect Rect, int? TargetPage, string? Uri);

/// <summary>Reads <c>/Link</c> annotations (GoTo page destinations and URI actions).</summary>
public static class LinkReader
{
    public static Dictionary<int, List<PdfLink>> ReadAll(byte[] source)
    {
        var result = new Dictionary<int, List<PdfLink>>();
        PdfDocument doc;
        try
        {
            using var input = new MemoryStream(source);
            doc = PdfReader.Open(input, PdfDocumentOpenMode.ReadOnly);
        }
        catch { return result; }

        using (doc)
        {
            for (int pi = 0; pi < doc.PageCount; pi++)
            {
                var annots = doc.Pages[pi].Elements.GetArray("/Annots");
                if (annots is null) continue;
                List<PdfLink>? list = null;
                foreach (var item in annots.Elements)
                {
                    var d = Resolve(item);
                    if (d is null || d.Elements.GetName("/Subtype") != "/Link") continue;
                    var rect = ReadRect(d);
                    if (rect is null) continue;

                    int? page = null; string? uri = null;
                    var action = d.Elements.GetDictionary("/A");
                    if (action is not null)
                    {
                        string s = action.Elements.GetName("/S");
                        if (s == "/URI") uri = action.Elements.ContainsKey("/URI") ? action.Elements.GetString("/URI") : null;
                        else if (s == "/GoTo") page = ResolvePage(doc, action.Elements.GetValue("/D"));
                    }
                    if (page is null && uri is null && d.Elements.ContainsKey("/Dest"))
                        page = ResolvePage(doc, d.Elements.GetValue("/Dest"));

                    if (page is null && uri is null) continue;
                    (list ??= new()).Add(new PdfLink(rect.Value, page, uri));
                }
                if (list is not null) result[pi] = list;
            }
        }
        return result;
    }

    private static int? ResolvePage(PdfDocument doc, PdfItem? destItem)
    {
        var dest = Deref(destItem);
        // Named destination (string/name) → look up in /Dests.
        if (dest is PdfString ps) dest = LookupNamedDest(doc, ps.Value);
        else if (dest is PdfName pn) dest = LookupNamedDest(doc, pn.Value.TrimStart('/'));
        if (dest is PdfArray arr && arr.Elements.Count > 0)
        {
            var first = arr.Elements[0];
            if (first is PdfReference pref) return PageIndexOf(doc, pref);
        }
        return null;
    }

    private static PdfItem? LookupNamedDest(PdfDocument doc, string name)
    {
        // Old-style /Dests dictionary in the catalog.
        var dests = doc.Internals.Catalog.Elements.GetDictionary("/Dests");
        if (dests is not null && dests.Elements.ContainsKey("/" + name))
        {
            var v = Deref(dests.Elements.GetValue("/" + name));
            if (v is PdfDictionary dd) return Deref(dd.Elements.GetValue("/D"));
            return v;
        }
        return null;
    }

    private static int? PageIndexOf(PdfDocument doc, PdfReference pageRef)
    {
        for (int i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Reference?.ObjectID == pageRef.ObjectID) return i;
        return null;
    }

    private static PdfDictionary? Resolve(PdfItem item) => item switch
    {
        PdfReference r => r.Value as PdfDictionary,
        PdfDictionary d => d,
        _ => null,
    };

    private static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static TextRect? ReadRect(PdfDictionary d)
    {
        var arr = d.Elements.GetArray("/Rect");
        if (arr is null || arr.Elements.Count < 4) return null;
        double[] v = new double[4];
        for (int i = 0; i < 4; i++)
        {
            var it = arr.Elements[i] is PdfReference r ? r.Value : arr.Elements[i];
            v[i] = it switch { PdfReal pr => pr.Value, PdfInteger pint => pint.Value, _ => 0 };
        }
        double l = System.Math.Min(v[0], v[2]), b = System.Math.Min(v[1], v[3]);
        double rr = System.Math.Max(v[0], v[2]), t = System.Math.Max(v[1], v[3]);
        return new TextRect(l, b, rr, t);
    }
}
