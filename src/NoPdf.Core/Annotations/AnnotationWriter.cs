using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NoPdf.Core.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace NoPdf.Core.Annotations;

/// <summary>
/// Writes NoPdf annotation models into a PDF as standard annotation objects using
/// PDFsharp. Every annotation gets an appearance stream (/AP) so it renders in any
/// viewer, including PDFium on reload.
/// </summary>
public static class AnnotationWriter
{
    /// <summary>Private annotation key holding the app's grouping. PDF has no notion of
    /// grouped markup, so it rides along as a custom entry other viewers ignore.</summary>
    public const string GroupKey = "/NoPdfGroup";

    public static void Save(string sourcePath, string destPath,
        IEnumerable<PdfAnnotationModel> annotations)
    {
        using var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
        AddAll(doc, annotations);
        doc.Save(destPath);
    }

    /// <summary>Adds annotations to an in-memory PDF and returns the new bytes.</summary>
    public static byte[] SaveToBytes(byte[] source, IEnumerable<PdfAnnotationModel> annotations)
    {
        using var input = new System.IO.MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        AddAll(doc, annotations);
        using var output = new System.IO.MemoryStream();
        doc.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static void AddAll(PdfDocument doc, IEnumerable<PdfAnnotationModel> annotations)
    {
        foreach (var ann in annotations)
        {
            if (ann.PageIndex < 0 || ann.PageIndex >= doc.PageCount) continue;
            var page = doc.Pages[ann.PageIndex];
            switch (ann)
            {
                case HighlightAnnotation h: AddHighlight(doc, page, h); break;
                case ImageAnnotation im: AddImage(doc, page, im); break;
                case SignatureAnnotation sig: AddSignature(doc, page, sig); break;
                case CalloutAnnotation c: AddCallout(doc, page, c); break;
                case FreeTextAnnotation f: AddFreeText(doc, page, f); break;
                case SquareAnnotation s: AddSquare(doc, page, s); break;
                case LineAnnotation l: AddLine(doc, page, l); break;
                case PolylineAnnotation p: AddPolyline(doc, page, p); break;
                case StickyNoteAnnotation n: AddStickyNote(doc, page, n); break;
            }
        }
    }

    // ---------------- Highlight ----------------

    private static void AddHighlight(PdfDocument doc, PdfPage page, HighlightAnnotation h)
    {
        if (h.Quads.Count == 0) return;
        var (l, b, r, t) = Extent(h.Quads);

        var annot = NewAnnot(doc, page, "/Highlight", l, b, r, t, h);
        annot.Elements["/QuadPoints"] = QuadPoints(doc, h.Quads);

        var sb = new StringBuilder();
        sb.Append("/GS0 gs\n").Append(Col(h.Color, false));
        foreach (var q in h.Quads)
            sb.Append(Re(q.Left, q.Bottom, q.Width, q.Height));
        sb.Append("f\n");
        annot.Elements["/AP"] = Appearance(doc, l, b, r, t, sb.ToString(), multiply: true);
    }

    // ---------------- Square ----------------

    private static void AddSquare(PdfDocument doc, PdfPage page, SquareAnnotation s)
    {
        double w = s.StrokeWidth, inset = w / 2;
        var rc = s.Rect;
        var annot = NewAnnot(doc, page, "/Square", rc.Left, rc.Bottom, rc.Right, rc.Top, s);
        annot.Elements["/BS"] = BorderStyle(doc, w);
        if (s.Interior is { } ic) annot.Elements["/IC"] = ColorArr(doc, ic);

        var sb = new StringBuilder();
        sb.Append(F(w)).Append(" w\n").Append(Col(s.Color, true));
        if (s.Interior is { } ic2) sb.Append(Col(ic2, false));
        sb.Append(Re(rc.Left + inset, rc.Bottom + inset, rc.Width - w, rc.Height - w));
        sb.Append(s.Interior is null ? "S\n" : "B\n");
        annot.Elements["/AP"] = Appearance(doc, rc.Left, rc.Bottom, rc.Right, rc.Top, sb.ToString());
    }

    // ---------------- Line / Arrow ----------------

    private static void AddLine(PdfDocument doc, PdfPage page, LineAnnotation ln)
    {
        double pad = ln.StrokeWidth + 14;
        double l = Math.Min(ln.Start.X, ln.End.X) - pad, b = Math.Min(ln.Start.Y, ln.End.Y) - pad;
        double r = Math.Max(ln.Start.X, ln.End.X) + pad, t = Math.Max(ln.Start.Y, ln.End.Y) + pad;

        var annot = NewAnnot(doc, page, "/Line", l, b, r, t, ln);
        var lArr = new PdfArray(doc);
        foreach (var v in new[] { ln.Start.X, ln.Start.Y, ln.End.X, ln.End.Y })
            lArr.Elements.Add(new PdfReal(v));
        annot.Elements["/L"] = lArr;
        annot.Elements["/BS"] = BorderStyle(doc, ln.StrokeWidth);
        if (ln.Arrow)
        {
            var le = new PdfArray(doc);
            le.Elements.Add(new PdfName("/None"));
            le.Elements.Add(new PdfName("/OpenArrow"));
            annot.Elements["/LE"] = le;
        }

        var sb = new StringBuilder();
        sb.Append(F(ln.StrokeWidth)).Append(" w\n").Append(Col(ln.Color, true)).Append(Col(ln.Color, false));
        sb.Append(F(ln.Start.X)).Append(' ').Append(F(ln.Start.Y)).Append(" m ")
          .Append(F(ln.End.X)).Append(' ').Append(F(ln.End.Y)).Append(" l S\n");
        if (ln.Arrow) sb.Append(Arrowhead(ln.Start, ln.End, ln.StrokeWidth));
        annot.Elements["/AP"] = Appearance(doc, l, b, r, t, sb.ToString());
    }

    // ---------------- Polyline / Polygon ----------------

    private static void AddPolyline(PdfDocument doc, PdfPage page, PolylineAnnotation p)
    {
        if (p.Points.Count < 2) return;
        double pad = p.StrokeWidth + 2;
        var (l, b, r, t) = Extent(p.Points.Select(pt => new TextRect(pt.X, pt.Y, pt.X, pt.Y)).ToList());
        l -= pad; b -= pad; r += pad; t += pad;

        var annot = NewAnnot(doc, page, p.Closed ? "/Polygon" : "/PolyLine", l, b, r, t, p);
        var verts = new PdfArray(doc);
        foreach (var pt in p.Points) { verts.Elements.Add(new PdfReal(pt.X)); verts.Elements.Add(new PdfReal(pt.Y)); }
        annot.Elements["/Vertices"] = verts;
        annot.Elements["/BS"] = BorderStyle(doc, p.StrokeWidth);

        var sb = new StringBuilder();
        sb.Append(F(p.StrokeWidth)).Append(" w\n").Append(Col(p.Color, true));
        sb.Append(F(p.Points[0].X)).Append(' ').Append(F(p.Points[0].Y)).Append(" m ");
        for (int i = 1; i < p.Points.Count; i++)
            sb.Append(F(p.Points[i].X)).Append(' ').Append(F(p.Points[i].Y)).Append(" l ");
        sb.Append(p.Closed ? "s\n" : "S\n");
        annot.Elements["/AP"] = Appearance(doc, l, b, r, t, sb.ToString());
    }

    // ---------------- FreeText ----------------

    private static void AddFreeText(PdfDocument doc, PdfPage page, FreeTextAnnotation f)
    {
        var rc = f.Rect;
        var annot = NewAnnot(doc, page, "/FreeText", rc.Left, rc.Bottom, rc.Right, rc.Top, f);
        annot.Elements.SetString("/DA", $"/Helv {F(f.FontSize)} Tf {Col3(f.TextColor)} rg");
        annot.Elements.SetInteger("/Q", 0);
        // Record the border width so the frame state survives a reload/strip round-trip.
        annot.Elements["/BS"] = BorderStyle(doc, f.Border ? Math.Max(1, f.StrokeWidth) : 0);
        annot.Elements["/AP"] = Appearance(doc, rc.Left, rc.Bottom, rc.Right, rc.Top,
            FreeTextContent(f), withFont: true, strokeAlpha: f.BorderOpacity);
    }

    private static void AddCallout(PdfDocument doc, PdfPage page, CalloutAnnotation c)
    {
        var rc = c.Rect;
        // Bounds must include the callout leader.
        double l = Math.Min(rc.Left, c.Tip.X), b = Math.Min(rc.Bottom, c.Tip.Y);
        double r = Math.Max(rc.Right, c.Tip.X), t = Math.Max(rc.Top, c.Tip.Y);
        if (c.Knee is { } k) { l = Math.Min(l, k.X); b = Math.Min(b, k.Y); r = Math.Max(r, k.X); t = Math.Max(t, k.Y); }
        double pad = c.StrokeWidth + 12;
        l -= pad; b -= pad; r += pad; t += pad;

        var annot = NewAnnot(doc, page, "/FreeText", l, b, r, t, c);
        annot.Elements.SetName("/IT", "/FreeTextCallout");
        annot.Elements.SetString("/DA", $"/Helv {F(c.FontSize)} Tf {Col3(c.TextColor)} rg");
        annot.Elements["/BS"] = BorderStyle(doc, c.Border ? Math.Max(1, c.StrokeWidth) : 0);
        var le = new PdfArray(doc); le.Elements.Add(new PdfName("/OpenArrow")); le.Elements.Add(new PdfName("/None"));
        annot.Elements["/LE"] = le;

        var attach = ClosestPointOnRect(rc, c.Knee ?? c.Tip);
        var cl = new PdfArray(doc);
        void Pt(PdfPoint p) { cl.Elements.Add(new PdfReal(p.X)); cl.Elements.Add(new PdfReal(p.Y)); }
        Pt(c.Tip); if (c.Knee is { } kk) Pt(kk); Pt(new PdfPoint(attach.X, attach.Y));
        annot.Elements["/CL"] = cl;

        var sb = new StringBuilder();
        // Leader line + arrow.
        sb.Append(F(c.StrokeWidth)).Append(" w\n").Append(Col(c.Color, true)).Append(Col(c.Color, false));
        sb.Append(F(c.Tip.X)).Append(' ').Append(F(c.Tip.Y)).Append(" m ");
        if (c.Knee is { } k2) sb.Append(F(k2.X)).Append(' ').Append(F(k2.Y)).Append(" l ");
        sb.Append(F(attach.X)).Append(' ').Append(F(attach.Y)).Append(" l S\n");
        sb.Append(Arrowhead(c.Knee ?? new PdfPoint(attach.X, attach.Y), c.Tip, c.StrokeWidth));
        sb.Append(FreeTextContent(c));
        annot.Elements["/AP"] = Appearance(doc, l, b, r, t, sb.ToString(), withFont: true, strokeAlpha: c.BorderOpacity);
    }

    private static string FreeTextContent(FreeTextAnnotation f)
    {
        var rc = f.Rect;
        var sb = new StringBuilder();
        if (f.Border)
        {
            double w = Math.Max(1, f.StrokeWidth), inset = w / 2;
            bool alpha = f.BorderOpacity < 0.999;
            if (alpha) sb.Append("q /GSca gs\n");
            sb.Append(F(w)).Append(" w\n").Append(Col(f.Color, true));
            sb.Append(Re(rc.Left + inset, rc.Bottom + inset, rc.Width - w, rc.Height - w)).Append("S\n");
            if (alpha) sb.Append("Q\n");
        }
        double pad = 3, leading = f.FontSize * 1.25;
        double x = rc.Left + pad, y = rc.Top - pad - f.FontSize;
        var lines = (f.Contents ?? "").Replace("\r", "").Split('\n');
        sb.Append("BT\n").Append("/Helv ").Append(F(f.FontSize)).Append(" Tf ")
          .Append(Col3(f.TextColor)).Append(" rg\n");
        sb.Append(F(x)).Append(' ').Append(F(y)).Append(" Td\n");
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append("0 ").Append(F(-leading)).Append(" Td\n");
            sb.Append('(').Append(EscapePdf(lines[i])).Append(") Tj\n");
        }
        sb.Append("ET\n");
        return sb.ToString();
    }

    // ---------------- Signature ----------------

    private const int LogoPx = 240;
    private static byte[]? _logoJpeg;

    private static byte[] LogoJpeg()
    {
        if (_logoJpeg is not null) return _logoJpeg;
        using var s = typeof(AnnotationWriter).Assembly
            .GetManifestResourceStream("NoPdf.Core.Assets.sig_logo.jpg");
        using var ms = new System.IO.MemoryStream();
        s!.CopyTo(ms);
        return _logoJpeg = ms.ToArray();
    }

    private static byte[]? _logoMask;

    /// <summary>Peak opacity of the watermark, 0..255. The stored mask only reaches ~33,
    /// which is invisible on white, so it is scaled up to this.</summary>
    private const int WatermarkPeak = 90;

    /// <summary>Raw 8-bit alpha (LogoPx²), 0 = transparent (white bg) .. 255 = opaque,
    /// normalised so the logo actually reads as a watermark.</summary>
    private static byte[] LogoMask()
    {
        if (_logoMask is not null) return _logoMask;
        using var s = typeof(AnnotationWriter).Assembly
            .GetManifestResourceStream("NoPdf.Core.Assets.sig_logo_mask.bin");
        using var ms = new System.IO.MemoryStream();
        s!.CopyTo(ms);
        var raw = ms.ToArray();

        byte peak = 0;
        foreach (var b in raw) if (b > peak) peak = b;
        if (peak is > 0 and < WatermarkPeak)
        {
            double k = WatermarkPeak / (double)peak;
            for (int i = 0; i < raw.Length; i++)
                raw[i] = (byte)Math.Min(255, Math.Round(raw[i] * k));
        }
        return _logoMask = raw;
    }

    /// <summary>zlib-wrapped deflate, as expected by PDF <c>/FlateDecode</c>.</summary>
    private static byte[] Flate(byte[] raw)
    {
        using var ms = new System.IO.MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
            ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static void AddSignature(PdfDocument doc, PdfPage page, SignatureAnnotation sig)
    {
        var rc = sig.Rect;
        var annot = NewAnnot(doc, page, "/Stamp", rc.Left, rc.Bottom, rc.Right, rc.Top, sig);
        // No /Name: on a stamp that names a PREDEFINED icon, and viewers that honour it
        // over the appearance stream drew nothing for a custom one. /AP is authoritative.

        // Faint watermark image XObject (JPEG / DCTDecode) with a soft mask so the
        // white background is transparent and the page shows through.
        var logo = new PdfDictionary(doc);
        doc.Internals.AddObject(logo);
        logo.Elements.SetName("/Type", "/XObject");
        logo.Elements.SetName("/Subtype", "/Image");
        logo.Elements.SetInteger("/Width", LogoPx);
        logo.Elements.SetInteger("/Height", LogoPx);
        logo.Elements.SetName("/ColorSpace", "/DeviceRGB");
        logo.Elements.SetInteger("/BitsPerComponent", 8);
        logo.Elements.SetName("/Filter", "/DCTDecode");
        logo.CreateStream(LogoJpeg());

        var mask = new PdfDictionary(doc);
        doc.Internals.AddObject(mask);
        mask.Elements.SetName("/Type", "/XObject");
        mask.Elements.SetName("/Subtype", "/Image");
        mask.Elements.SetInteger("/Width", LogoPx);
        mask.Elements.SetInteger("/Height", LogoPx);
        mask.Elements.SetName("/ColorSpace", "/DeviceGray");
        mask.Elements.SetInteger("/BitsPerComponent", 8);
        mask.Elements.SetName("/Filter", "/FlateDecode");
        mask.CreateStream(Flate(LogoMask()));
        logo.Elements["/SMask"] = mask.Reference!;

        // Appearance form with the image + Helvetica font in resources.
        var form = new PdfDictionary(doc);
        doc.Internals.AddObject(form);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Rect(doc, rc.Left, rc.Bottom, rc.Right, rc.Top);
        form.Elements.SetInteger("/FormType", 1);
        var xobj = new PdfDictionary(doc); xobj.Elements["/Sig"] = logo.Reference!;
        var helv = new PdfDictionary(doc);
        helv.Elements.SetName("/Type", "/Font"); helv.Elements.SetName("/Subtype", "/Type1");
        helv.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(doc); fonts.Elements["/Helv"] = helv;
        var res = new PdfDictionary(doc);
        res.Elements["/XObject"] = xobj; res.Elements["/Font"] = fonts;
        if (sig.BorderOpacity < 0.999)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements.SetName("/Type", "/ExtGState");
            gs.Elements.SetReal("/CA", sig.BorderOpacity);
            gs.Elements.SetReal("/ca", sig.BorderOpacity);
            var eg = new PdfDictionary(doc); eg.Elements["/GSca"] = gs;
            res.Elements["/ExtGState"] = eg;
        }
        form.Elements["/Resources"] = res;

        var sb = new StringBuilder();
        // Watermark stretched to the signature frame, so it takes the shape the user drew.
        sb.Append("q ").Append(F(rc.Width)).Append(" 0 0 ").Append(F(rc.Height)).Append(' ')
          .Append(F(rc.Left)).Append(' ').Append(F(rc.Bottom)).Append(" cm /Sig Do Q\n");
        // border (honours width + opacity)
        double bw = Math.Max(0.5, sig.StrokeWidth), inset = bw / 2;
        bool bAlpha = sig.BorderOpacity < 0.999;
        if (bAlpha) sb.Append("q /GSca gs\n");
        sb.Append(F(bw)).Append(" w\n").Append(Col(sig.Color, true));
        sb.Append(Re(rc.Left + inset, rc.Bottom + inset, rc.Width - bw, rc.Height - bw)).Append("S\n");
        if (bAlpha) sb.Append("Q\n");
        // text
        double pad = 6;
        double x = rc.Left + pad, top = rc.Top - pad;
        sb.Append("BT\n");
        sb.Append("/Helv 12 Tf ").Append(Col3(sig.Color)).Append(" rg\n");
        sb.Append(F(x)).Append(' ').Append(F(top - 12)).Append(" Td (").Append(EscapePdf(sig.SignerName)).Append(") Tj\n");
        double y = top - 12;
        if (!string.IsNullOrWhiteSpace(sig.Contents))
        {
            y -= 16;
            sb.Append("/Helv 10 Tf 0.13 0.13 0.13 rg\n");
            sb.Append("0 -16 Td (").Append(EscapePdf(sig.Contents!)).Append(") Tj\n");
        }
        sb.Append("/Helv 8 Tf 0.4 0.4 0.4 rg\n");
        sb.Append("0 -14 Td (Signed ").Append(EscapePdf(sig.Signed.ToString("yyyy-MM-dd HH:mm"))).Append(") Tj\n");
        sb.Append("ET\n");
        form.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));

        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        annot.Elements["/AP"] = ap;
    }

    // ---------------- Image ----------------

    private static void AddImage(PdfDocument doc, PdfPage page, ImageAnnotation img)
    {
        if (img.ImageData.Length == 0) return;
        PdfSharp.Drawing.XImage xi;
        // Keep the backing stream alive (referenced by the XImage) until doc.Save().
        try { xi = PdfSharp.Drawing.XImage.FromStream(new System.IO.MemoryStream(img.ImageData)); }
        catch { return; }

        using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page, PdfSharp.Drawing.XGraphicsPdfPageOptions.Append);
        var rc = img.Rect;
        double pageH = page.Height.Point;
        // Model rect is PDF page space (origin bottom-left); XGraphics is top-left origin.
        double x = rc.Left, y = pageH - rc.Top, w = rc.Width, h = rc.Height;
        gfx.DrawImage(xi, x, y, w, h);
        if (img.Border)
        {
            var pen = new PdfSharp.Drawing.XPen(
                PdfSharp.Drawing.XColor.FromArgb(img.Color.R, img.Color.G, img.Color.B),
                Math.Max(0.5, img.StrokeWidth));
            gfx.DrawRectangle(pen, x, y, w, h);
        }
    }

    // ---------------- Sticky note ----------------

    private static void AddStickyNote(PdfDocument doc, PdfPage page, StickyNoteAnnotation n)
    {
        double sz = StickyNoteAnnotation.IconSize;
        double l = n.Position.X, t = n.Position.Y, r = l + sz, b = t - sz;
        var annot = NewAnnot(doc, page, "/Text", l, b, r, t, n);
        annot.Elements.SetName("/Name", "/Comment");
        annot.Elements.SetBoolean("/Open", false);

        var sb = new StringBuilder();
        var c = n.Color;
        sb.Append(Col(new AnnotColor(255, 214, 92), false));      // note fill
        sb.Append(Re(l, b, sz, sz)).Append("f\n");
        sb.Append("1 w\n").Append(Col(new AnnotColor(120, 90, 0), true));
        sb.Append(Re(l + 0.5, b + 0.5, sz - 1, sz - 1)).Append("S\n");
        // Three "text" lines.
        for (int i = 0; i < 3; i++)
        {
            double yy = t - 6 - i * 4;
            sb.Append(F(l + 4)).Append(' ').Append(F(yy)).Append(" m ")
              .Append(F(r - 4)).Append(' ').Append(F(yy)).Append(" l S\n");
        }
        annot.Elements["/AP"] = Appearance(doc, l, b, r, t, sb.ToString());
    }

    // ---------------- Shared helpers ----------------

    private static PdfDictionary NewAnnot(PdfDocument doc, PdfPage page, string subtype,
        double l, double b, double r, double t, PdfAnnotationModel model)
    {
        var annot = new PdfDictionary(doc);
        doc.Internals.AddObject(annot);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", subtype);
        annot.Elements["/Rect"] = Rect(doc, l, b, r, t);
        annot.Elements["/C"] = ColorArr(doc, model.Color);
        annot.Elements.SetInteger("/F", 4);
        if (!string.IsNullOrEmpty(model.Author)) annot.Elements.SetString("/T", model.Author);
        if (!string.IsNullOrEmpty(model.Contents)) annot.Elements.SetString("/Contents", model.Contents);
        annot.Elements.SetString("/M", "D:" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        if (model.GroupPath.Count > 0)
            annot.Elements.SetString(GroupKey, GroupPathCodec.Write(model.GroupPath));

        var annots = page.Elements.GetArray("/Annots");
        if (annots is null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
        annots.Elements.Add(annot.Reference!);
        return annot;
    }

    private static PdfDictionary Appearance(PdfDocument doc, double l, double b, double r, double t,
        string content, bool withFont = false, bool multiply = false, double strokeAlpha = 1.0)
    {
        var form = new PdfDictionary(doc);
        doc.Internals.AddObject(form);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Rect(doc, l, b, r, t);
        form.Elements.SetInteger("/FormType", 1);

        var res = new PdfDictionary(doc);
        if (multiply)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements.SetName("/Type", "/ExtGState");
            gs.Elements.SetName("/BM", "/Multiply");
            var eg = new PdfDictionary(doc); eg.Elements["/GS0"] = gs;
            res.Elements["/ExtGState"] = eg;
        }
        if (strokeAlpha < 0.999)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements.SetName("/Type", "/ExtGState");
            gs.Elements.SetReal("/CA", strokeAlpha);
            gs.Elements.SetReal("/ca", strokeAlpha);
            var eg = res.Elements.GetDictionary("/ExtGState") ?? new PdfDictionary(doc);
            eg.Elements["/GSca"] = gs;
            res.Elements["/ExtGState"] = eg;
        }
        if (withFont)
        {
            var helv = new PdfDictionary(doc);
            helv.Elements.SetName("/Type", "/Font");
            helv.Elements.SetName("/Subtype", "/Type1");
            helv.Elements.SetName("/BaseFont", "/Helvetica");
            var fonts = new PdfDictionary(doc); fonts.Elements["/Helv"] = helv;
            res.Elements["/Font"] = fonts;
        }
        form.Elements["/Resources"] = res;
        form.CreateStream(Encoding.ASCII.GetBytes(content));

        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        return ap;
    }

    private static string Arrowhead(PdfPoint from, PdfPoint to, double w)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return "";
        double ux = dx / len, uy = dy / len;      // direction
        double px = -uy, py = ux;                  // perpendicular
        double head = 8 + w * 2, half = 3 + w;
        double bx = to.X - ux * head, by = to.Y - uy * head;
        var sb = new StringBuilder();
        sb.Append(F(to.X)).Append(' ').Append(F(to.Y)).Append(" m ");
        sb.Append(F(bx + px * half)).Append(' ').Append(F(by + py * half)).Append(" l ");
        sb.Append(F(bx - px * half)).Append(' ').Append(F(by - py * half)).Append(" l f\n");
        return sb.ToString();
    }

    private static PdfPoint ClosestPointOnRect(TextRect rc, PdfPoint p)
    {
        double x = Math.Clamp(p.X, rc.Left, rc.Right);
        double y = Math.Clamp(p.Y, rc.Bottom, rc.Top);
        // Snap to the nearest edge so the leader attaches to the border, not the interior.
        double dl = Math.Abs(x - rc.Left), dr = Math.Abs(x - rc.Right);
        double db = Math.Abs(y - rc.Bottom), dt = Math.Abs(y - rc.Top);
        double min = Math.Min(Math.Min(dl, dr), Math.Min(db, dt));
        if (min == dl) x = rc.Left; else if (min == dr) x = rc.Right;
        else if (min == db) y = rc.Bottom; else y = rc.Top;
        return new PdfPoint(x, y);
    }

    private static (double l, double b, double r, double t) Extent(IReadOnlyList<TextRect> rects)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var q in rects)
        {
            if (q.Left < l) l = q.Left; if (q.Bottom < b) b = q.Bottom;
            if (q.Right > r) r = q.Right; if (q.Top > t) t = q.Top;
        }
        return (l, b, r, t);
    }

    private static PdfArray Rect(PdfDocument doc, double l, double b, double r, double t)
    {
        var a = new PdfArray(doc);
        a.Elements.Add(new PdfReal(l)); a.Elements.Add(new PdfReal(b));
        a.Elements.Add(new PdfReal(r)); a.Elements.Add(new PdfReal(t));
        return a;
    }

    private static PdfArray QuadPoints(PdfDocument doc, IReadOnlyList<TextRect> quads)
    {
        var a = new PdfArray(doc);
        foreach (var q in quads)
        {
            a.Elements.Add(new PdfReal(q.Left));  a.Elements.Add(new PdfReal(q.Top));
            a.Elements.Add(new PdfReal(q.Right)); a.Elements.Add(new PdfReal(q.Top));
            a.Elements.Add(new PdfReal(q.Left));  a.Elements.Add(new PdfReal(q.Bottom));
            a.Elements.Add(new PdfReal(q.Right)); a.Elements.Add(new PdfReal(q.Bottom));
        }
        return a;
    }

    private static PdfArray ColorArr(PdfDocument doc, AnnotColor c)
    {
        var a = new PdfArray(doc);
        a.Elements.Add(new PdfReal(c.Rf)); a.Elements.Add(new PdfReal(c.Gf)); a.Elements.Add(new PdfReal(c.Bf));
        return a;
    }

    private static PdfDictionary BorderStyle(PdfDocument doc, double w)
    {
        var bs = new PdfDictionary(doc);
        bs.Elements.SetName("/Type", "/Border");
        bs.Elements.SetReal("/W", w);
        bs.Elements.SetName("/S", "/S");
        return bs;
    }

    private static string Col(AnnotColor c, bool stroke)
        => $"{Col3(c)} {(stroke ? "RG" : "rg")}\n";

    private static string Col3(AnnotColor c) => $"{F(c.Rf)} {F(c.Gf)} {F(c.Bf)}";

    private static string Re(double x, double y, double w, double h)
        => $"{F(x)} {F(y)} {F(w)} {F(h)} re ";

    private static string EscapePdf(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
