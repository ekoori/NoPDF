using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PdfSharp.Pdf;

namespace NoPdf.Core.Import;

/// <summary>
/// Renders plain text into a simple paginated PDF (used for the help tab). Uses the
/// base-14 fonts via raw content streams, so it needs no font resolver and works the
/// same on every platform.
/// </summary>
public static class TextDocument
{
    /// <summary>A line of text; headings are drawn larger and bold.</summary>
    public readonly record struct Line(string Text, bool Heading = false);

    private const double Margin = 56; // A4 points

    /// <summary>Characters that fit on one Courier line at the body size, for callers
    /// that need to wrap text themselves (Courier is 0.6 em wide).</summary>
    public static int LineChars(bool landscape = false)
        => (int)(((landscape ? 842.0 : 595.0) - 2 * Margin) / (9.5 * 0.6));

    public static byte[] Build(string title, IEnumerable<Line> lines, bool landscape = false)
    {
        double pageW = landscape ? 842 : 595, pageH = landscape ? 595 : 842;
        var doc = new PdfDocument();
        doc.Info.Title = title;

        var sb = new StringBuilder();
        double y = pageH - Margin;
        PdfPage page = NewPage(doc, pageW, pageH);

        void Flush()
        {
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
            sb.Clear();
        }
        void Break()
        {
            Flush();
            page = NewPage(doc, pageW, pageH);
            y = pageH - Margin;
        }
        void Draw(string text, string font, double size)
        {
            sb.Append("BT /").Append(font).Append(' ').Append(F(size)).Append(" Tf 0 0 0 rg ")
              .Append(F(Margin)).Append(' ').Append(F(y)).Append(" Td (")
              .Append(Escape(text)).Append(") Tj ET\n");
        }

        Draw(title, "FB", 20);
        y -= 32;

        foreach (var line in lines)
        {
            double lead = line.Heading ? 22 : 13;
            if (y - lead < Margin) Break();
            if (line.Heading) y -= 8;
            Draw(line.Text, line.Heading ? "FB" : "FC", line.Heading ? 14 : 9.5);
            y -= lead;
        }
        Flush();

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static PdfPage NewPage(PdfDocument doc, double pageW, double pageH)
    {
        var page = doc.AddPage();
        page.Width = XUnitFromPoint(pageW);
        page.Height = XUnitFromPoint(pageH);

        // Base-14 fonts: FB = Helvetica-Bold (titles), FC = Courier (body).
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/FB"] = Base14(doc, "/Helvetica-Bold");
        fonts.Elements["/FC"] = Base14(doc, "/Courier");
        var res = new PdfDictionary(doc);
        res.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = res;
        return page;
    }

    private static PdfSharp.Drawing.XUnit XUnitFromPoint(double v) => PdfSharp.Drawing.XUnit.FromPoint(v);

    private static PdfDictionary Base14(PdfDocument doc, string baseFont)
    {
        var f = new PdfDictionary(doc);
        f.Elements.SetName("/Type", "/Font");
        f.Elements.SetName("/Subtype", "/Type1");
        f.Elements.SetName("/BaseFont", baseFont);
        f.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        return f;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            // Transliterate the typographic characters we actually use to ASCII.
            string? sub = ch switch
            {
                '—' or '–' => "-",     // em/en dash
                '…' => "...",               // ellipsis
                '‘' or '’' => "'",     // curly single quotes
                '“' or '”' => "\"",   // curly double quotes
                ' ' => " ",                 // nbsp
                _ => null,
            };
            if (sub is not null) { sb.Append(sub); continue; }

            if (ch is '\\' or '(' or ')') sb.Append('\\').Append(ch);
            else if (ch is '\n' or '\r' or '\t') sb.Append(' ');
            else if (ch < 32 || ch > 126) sb.Append('?'); // content stream is written as ASCII
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
