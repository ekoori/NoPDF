using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NoPdf.Core.Signing;

/// <summary>Where and how a signature should show on the page.</summary>
public sealed record SignatureAppearance(
    int PageIndex, double Left, double Bottom, double Right, double Top,
    string SignerName, string Reason, DateTime When);

/// <summary>
/// Signs a PDF by appending an incremental update, so signatures already in the document keep
/// verifying. <see cref="SignatureService"/> re-saves the whole file through PDFsharp, which
/// rewrites the bytes an earlier signature hashed and silently invalidates it — fine for the
/// first signature on a document, useless for anything counter-signed.
///
/// The visible stamp is the signature field's own appearance stream rather than a separate
/// annotation next to it. That is the point: it is covered by the signature, so it cannot be
/// moved or deleted without breaking it, and viewers show it as the signature rather than as
/// unrelated markup that happens to look like one.
/// </summary>
public static class IncrementalSigner
{
    /// <summary>Bytes reserved for the PKCS#7 blob, as hex digits (so half this many bytes).</summary>
    private const int SignatureHexLength = 16384;

    /// <summary>
    /// Appends a signature. Returns false with a reason when the file is not one that can be
    /// extended safely — the caller should fall back to a full rewrite and warn that earlier
    /// signatures will not survive it.
    /// </summary>
    public static bool TrySign(byte[] source, string destPath, X509Certificate2 cert,
        SignatureAppearance appearance, out string? error)
    {
        var w = PdfIncrementalWriter.TryCreate(source, out error);
        if (w is null) return false;

        string? catalog = w.GetObjectText(w.RootObject);
        if (catalog is null) { error = "could not read the document catalog"; return false; }

        int pagesRef = Reference(catalog, "/Pages");
        if (pagesRef <= 0) { error = "no page tree"; return false; }
        int pageNum = FindPage(w, pagesRef, appearance.PageIndex);
        if (pageNum <= 0) { error = "could not locate page " + (appearance.PageIndex + 1); return false; }

        string? pageText = w.GetObjectText(pageNum);
        if (pageText is null) { error = "could not read the page"; return false; }

        // ---- allocate the new objects ----
        int sigNum = w.AllocateObject();
        int widgetNum = w.AllocateObject();
        int apNum = w.AllocateObject();

        // Appearance: a form XObject drawn in its own coordinate space, mapped onto /Rect.
        double width = Math.Abs(appearance.Right - appearance.Left);
        double height = Math.Abs(appearance.Top - appearance.Bottom);
        string content = AppearanceContent(appearance, width, height);
        w.AddObject(apNum,
            "<</Type/XObject/Subtype/Form/FormType 1" +
            $"/BBox[0 0 {N(width)} {N(height)}]" +
            "/Resources<</Font<</F1 <</Type/Font/Subtype/Type1/BaseFont/Helvetica" +
            "/Encoding/WinAnsiEncoding>>>>>>" +
            $"/Length {Encoding.Latin1.GetByteCount(content)}>>\nstream\n{content}\nendstream");

        // The signature dictionary. /ByteRange and /Contents are fixed-width placeholders so
        // they can be overwritten in place once the file's final layout is known.
        string byteRangePlaceholder = $"[0 {Pad(0)} {Pad(0)} {Pad(0)}]";
        w.AddObject(sigNum,
            "<</Type/Sig/Filter/Adobe.PPKLite/SubFilter/adbe.pkcs7.detached" +
            $"/ByteRange{byteRangePlaceholder}" +
            $"/Contents<{new string('0', SignatureHexLength)}>" +
            $"/M({PdfDate(appearance.When)})" +
            $"/Name({Escape(appearance.SignerName)})" +
            $"/Reason({Escape(appearance.Reason)})" +
            "/Prop_Build<</App<</Name/noPDF>>>>>>");

        // Field and widget merged into one object, which is how a single-widget field is
        // normally written.
        w.AddObject(widgetNum,
            "<</Type/Annot/Subtype/Widget/FT/Sig" +
            $"/T({Escape("Signature_" + DateTime.UtcNow.Ticks)})" +
            $"/V {sigNum} 0 R/P {pageNum} 0 R" +
            $"/Rect[{N(Math.Min(appearance.Left, appearance.Right))} {N(Math.Min(appearance.Bottom, appearance.Top))} " +
            $"{N(Math.Max(appearance.Left, appearance.Right))} {N(Math.Max(appearance.Bottom, appearance.Top))}]" +
            "/F 4" +                                  // print
            $"/AP<</N {apNum} 0 R>>>>");

        // ---- supersede the page (add the widget to /Annots) and the catalog (/AcroForm) ----
        w.AddObject(pageNum, AddToAnnots(pageText, widgetNum));

        int acroRef = Reference(catalog, "/AcroForm");
        if (acroRef > 0)
        {
            string? acro = w.GetObjectText(acroRef);
            if (acro is null) { error = "could not read the existing form"; return false; }
            w.AddObject(acroRef, AddToFields(acro, widgetNum));
        }
        else
        {
            int acroNum = w.AllocateObject();
            w.AddObject(acroNum, $"<</Fields[{widgetNum} 0 R]/SigFlags 3>>");
            w.AddObject(w.RootObject, InsertIntoDict(catalog, $"/AcroForm {acroNum} 0 R"));
        }

        // ---- lay the file out, then fill in what depends on the layout ----
        var bytes = w.Build();
        if (!FillSignature(bytes, cert, out error)) return false;

        File.WriteAllBytes(destPath, bytes);
        return true;
    }

    /// <summary>
    /// Computes /ByteRange over the finished file, signs those bytes and writes the PKCS#7 into
    /// the /Contents placeholder. The signature covers everything except its own hex string.
    /// </summary>
    private static bool FillSignature(byte[] bytes, X509Certificate2 cert, out string? error)
    {
        error = null;
        string text = PdfIncrementalWriter.Latin1(bytes);

        int hexStart = text.LastIndexOf("/Contents<", StringComparison.Ordinal);
        if (hexStart < 0) { error = "signature placeholder missing"; return false; }
        hexStart += "/Contents<".Length;
        int hexEnd = text.IndexOf('>', hexStart);
        if (hexEnd < 0) { error = "signature placeholder malformed"; return false; }

        // The two signed spans: everything before the '<' and everything after the '>'.
        int a = hexStart - 1;                 // include the '<'
        int b = hexEnd + 1;                   // start again at the '>'
        int lenA = a;
        int lenB = bytes.Length - b;

        // The signature's own /ByteRange is the last one before its /Contents.
        int brStart = text.LastIndexOf("/ByteRange[", hexStart, StringComparison.Ordinal);
        if (brStart < 0) brStart = text.IndexOf("/ByteRange[", StringComparison.Ordinal);
        if (brStart < 0) { error = "byte range placeholder missing"; return false; }
        int brEnd = text.IndexOf(']', brStart);
        if (brEnd < 0) { error = "byte range placeholder malformed"; return false; }

        string byteRange = $"[0 {Pad(lenA)} {Pad(b)} {Pad(lenB)}]";
        if (byteRange.Length != brEnd - brStart - "/ByteRange".Length + 1)
        {
            error = "byte range placeholder is the wrong size";
            return false;
        }
        var brBytes = Encoding.Latin1.GetBytes(byteRange);
        Array.Copy(brBytes, 0, bytes, brStart + "/ByteRange".Length, brBytes.Length);

        // Hash exactly the bytes the range names.
        var signedData = new byte[lenA + lenB];
        Array.Copy(bytes, 0, signedData, 0, lenA);
        Array.Copy(bytes, b, signedData, lenA, lenB);

        byte[] pkcs7;
        try
        {
            var signed = new SignedCms(new ContentInfo(signedData), detached: true);
            var signer = new CmsSigner(cert)
            {
                IncludeOption = X509IncludeOption.WholeChain,
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            };
            // Self-asserted signing time. Not a trusted timestamp — that needs a TSA — but
            // without it a verifier has no date at all.
            signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
            signed.ComputeSignature(signer, silent: true);
            pkcs7 = signed.Encode();
        }
        catch (Exception ex) { error = "could not create the signature: " + ex.Message; return false; }

        string hex = Convert.ToHexString(pkcs7);
        if (hex.Length > SignatureHexLength)
        { error = $"signature is larger than the reserved space ({hex.Length} > {SignatureHexLength})"; return false; }

        // Pad with zeros; a PKCS#7 blob shorter than the reservation is normal and readers
        // stop at the DER length.
        hex = hex.PadRight(SignatureHexLength, '0');
        var hexBytes = Encoding.Latin1.GetBytes(hex);
        Array.Copy(hexBytes, 0, bytes, hexStart, hexBytes.Length);
        return true;
    }

    /// <summary>The signature's visible face: a framed panel with who signed, why and when.</summary>
    private static string AppearanceContent(SignatureAppearance a, double w, double h)
    {
        var sb = new StringBuilder();
        sb.Append("q\n");
        // Panel and frame.
        sb.Append("0.97 0.97 0.99 rg\n").Append($"0 0 {N(w)} {N(h)} re f\n");
        sb.Append("0.12 0.35 0.65 RG\n1 w\n").Append($"0.5 0.5 {N(w - 1)} {N(h - 1)} re S\n");

        double pad = Math.Min(6, w / 12);
        double size = Math.Clamp(h / 5.5, 6, 12);
        double y = h - pad - size;

        sb.Append("BT\n0.10 0.20 0.40 rg\n");
        sb.Append($"/F1 {N(size * 1.05)} Tf\n");
        sb.Append($"1 0 0 1 {N(pad)} {N(y)} Tm\n({Escape(Fit(a.SignerName, w - 2 * pad, size * 1.05))}) Tj\n");

        sb.Append("0.25 0.25 0.30 rg\n");
        sb.Append($"/F1 {N(size * 0.8)} Tf\n");
        if (!string.IsNullOrWhiteSpace(a.Reason))
        {
            y -= size * 1.25;
            sb.Append($"1 0 0 1 {N(pad)} {N(y)} Tm\n({Escape(Fit(a.Reason, w - 2 * pad, size * 0.8))}) Tj\n");
        }
        y -= size * 1.25;
        sb.Append($"1 0 0 1 {N(pad)} {N(y)} Tm\n({Escape(a.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}) Tj\n");
        sb.Append("ET\nQ\n");
        return sb.ToString();
    }

    /// <summary>Crude width fit for Helvetica (~0.5em average), so long names don't overflow.</summary>
    private static string Fit(string text, double width, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int max = Math.Max(1, (int)(width / (fontSize * 0.5)));
        return text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
    }

    // ---------- dictionary surgery on raw object text ----------

    /// <summary>Adds a reference to a page's /Annots, creating the array if there isn't one.</summary>
    private static string AddToAnnots(string pageText, int widget) => AddToArray(pageText, "/Annots", widget);

    private static string AddToFields(string acroText, int widget)
    {
        string s = AddToArray(acroText, "/Fields", widget);
        // A document with signature fields should say so.
        if (!s.Contains("/SigFlags", StringComparison.Ordinal))
            s = InsertIntoDict(s, "/SigFlags 3");
        return s;
    }

    private static string AddToArray(string dict, string key, int objNumber)
    {
        int i = dict.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return InsertIntoDict(dict, $"{key}[{objNumber} 0 R]");

        int after = i + key.Length;
        while (after < dict.Length && char.IsWhiteSpace(dict[after])) after++;
        if (after < dict.Length && dict[after] == '[')
        {
            int close = MatchBracket(dict, after);
            if (close < 0) return InsertIntoDict(dict, $"{key}[{objNumber} 0 R]");
            return dict[..close] + $" {objNumber} 0 R" + dict[close..];
        }

        // An indirect array (/Annots 12 0 R) would need that object superseded instead;
        // rather than guess, leave it and let the caller fall back.
        throw new NotSupportedException($"{key} is an indirect reference, which this writer does not rewrite");
    }

    private static int MatchBracket(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Inserts an entry just inside a dictionary's closing &gt;&gt;.</summary>
    private static string InsertIntoDict(string dict, string entry)
    {
        int close = dict.LastIndexOf(">>", StringComparison.Ordinal);
        return close < 0 ? dict : dict[..close] + entry + dict[close..];
    }

    /// <summary>Walks the page tree to the object number of a page by index.</summary>
    private static int FindPage(PdfIncrementalWriter w, int node, int wanted)
    {
        int seen = 0;
        return Walk(node, ref seen);

        int Walk(int num, ref int index)
        {
            string? text = w.GetObjectText(num);
            if (text is null) return -1;

            if (text.Contains("/Type/Page", StringComparison.Ordinal)
                && !text.Contains("/Type/Pages", StringComparison.Ordinal))
                return index++ == wanted ? num : -1;

            int kids = text.IndexOf("/Kids", StringComparison.Ordinal);
            if (kids < 0) return -1;
            int open = text.IndexOf('[', kids);
            int close = MatchBracket(text, open);
            if (open < 0 || close < 0) return -1;

            foreach (int child in References(text[(open + 1)..close]))
            {
                int found = Walk(child, ref index);
                if (found > 0) return found;
            }
            return -1;
        }
    }

    /// <summary>Object numbers from a run of "N 0 R" references.</summary>
    private static System.Collections.Generic.IEnumerable<int> References(string s)
    {
        var parts = s.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 2 < parts.Length; i++)
            if (parts[i + 2] == "R" && int.TryParse(parts[i], out int n)) yield return n;
    }

    private static int Reference(string dict, string key)
    {
        int i = dict.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        string rest = dict[(i + key.Length)..].TrimStart();
        string digits = new(rest.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 ? int.Parse(digits, CultureInfo.InvariantCulture) : -1;
    }

    // ---------- formatting ----------

    private static string Pad(long v) => v.ToString("D10", CultureInfo.InvariantCulture);
    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string PdfDate(DateTime when)
    {
        var local = when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when;
        var off = TimeZoneInfo.Local.GetUtcOffset(local);
        char sign = off < TimeSpan.Zero ? '-' : '+';
        return $"D:{local:yyyyMMddHHmmss}{sign}{Math.Abs(off.Hours):D2}'{Math.Abs(off.Minutes):D2}'";
    }

    /// <summary>Escapes a PDF literal string.</summary>
    private static string Escape(string? s) => (s ?? "")
        .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")
        .Replace("\r", " ").Replace("\n", " ");
}
