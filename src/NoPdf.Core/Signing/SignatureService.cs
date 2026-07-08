using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace NoPdf.Core.Signing;

/// <summary>Cryptographically signs a PDF with an X.509 certificate (PKCS#7 / PAdES).</summary>
public static class SignatureService
{
    /// <summary>Loads a PKCS#12 (.pfx) certificate with its private key.</summary>
    public static X509Certificate2 LoadCertificate(string pfxPath, string password)
        => X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    /// <summary>Signs <paramref name="source"/> and writes the signed PDF to <paramref name="destPath"/>.</summary>
    public static void Sign(byte[] source, string destPath, X509Certificate2 cert,
        string? reason, string? location)
    {
        using var input = new MemoryStream(source);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        var options = new DigitalSignatureOptions
        {
            AppName = "noPDF",
            Reason = reason ?? "",
            Location = location ?? "",
            ContactInfo = "",
            PageIndex = 0,
            Rectangle = new XRect(0, 0, 0, 0), // invisible field; the visible stamp is separate
        };
        _ = DigitalSignatureHandler.ForDocument(doc, new CertSigner(cert), options);
        doc.Save(destPath);
    }

    private sealed class CertSigner : IDigitalSigner
    {
        private readonly X509Certificate2 _cert;
        public CertSigner(X509Certificate2 cert) => _cert = cert;

        public string CertificateName => _cert.GetNameInfo(X509NameType.SimpleName, false);

        public Task<int> GetSignatureSizeAsync() => Task.FromResult(16384);

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            byte[] data;
            long len = stream.Length;
            if (len > 0)
            {
                data = new byte[len];
                int off = 0, r;
                while (off < len && (r = stream.Read(data, off, (int)(len - off))) > 0) off += r;
            }
            else
            {
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) ms.Write(buffer, 0, read);
                data = ms.ToArray();
            }
            var content = new ContentInfo(data);
            var signed = new SignedCms(content, detached: true);
            var signer = new CmsSigner(_cert)
            {
                IncludeOption = X509IncludeOption.WholeChain,
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            };
            signed.ComputeSignature(signer, silent: true);
            return Task.FromResult(signed.Encode());
        }
    }
}
