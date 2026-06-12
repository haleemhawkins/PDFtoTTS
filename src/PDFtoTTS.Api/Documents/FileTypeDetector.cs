using System.Text;
using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Api.Documents;

/// <summary>
/// Detects document type by content (magic bytes), not extension (design §2,
/// "validate by content"). Returns null for unsupported content.
/// </summary>
public static class FileTypeDetector
{
    private static readonly byte[] PdfMagic = "%PDF"u8.ToArray();
    private static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 }; // "PK\x03\x04"

    public static DocumentType? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PdfMagic)) return DocumentType.Pdf;

        // EPUB is a ZIP whose first entry is an uncompressed "mimetype" file
        // containing "application/epub+zip" near the start of the archive.
        if (head.StartsWith(ZipMagic))
        {
            int scan = Math.Min(head.Length, 256);
            string ascii = Encoding.ASCII.GetString(head[..scan]);
            if (ascii.Contains("application/epub+zip")) return DocumentType.Epub;
        }

        return null;
    }

    public static string Extension(DocumentType type) =>
        type == DocumentType.Pdf ? ".pdf" : ".epub";
}
