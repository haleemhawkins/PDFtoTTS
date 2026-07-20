using System.IO.Compression;
using System.Text;

namespace PDFtoTTS.Core.Tests.Fixtures;

/// <summary>
/// Builds a minimal, valid EPUB 2 in memory so EPUB extraction can be tested
/// without committing a binary fixture. The caller supplies the inner HTML of
/// the single chapter body.
/// </summary>
public static class EpubFixtureBuilder
{
    public static byte[] Build(string chapterBodyHtml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // mimetype must be the first entry and stored uncompressed.
            Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add(zip, "META-INF/container.xml", Container);
            Add(zip, "OEBPS/content.opf", Opf);
            Add(zip, "OEBPS/toc.ncx", Ncx);
            Add(zip, "OEBPS/chapter1.xhtml", Chapter(chapterBodyHtml));
        }

        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private const string Container = """
        <?xml version="1.0"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private const string Opf = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="bookid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>Test Book</dc:title>
            <dc:language>en</dc:language>
            <dc:identifier id="bookid">urn:uuid:pdftotts-test</dc:identifier>
          </metadata>
          <manifest>
            <item id="chap1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
            <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
          </manifest>
          <spine toc="ncx">
            <itemref idref="chap1"/>
          </spine>
        </package>
        """;

    private const string Ncx = """
        <?xml version="1.0" encoding="utf-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head><meta name="dtb:uid" content="urn:uuid:pdftotts-test"/></head>
          <docTitle><text>Test Book</text></docTitle>
          <navMap>
            <navPoint id="np1" playOrder="1">
              <navLabel><text>Chapter 1</text></navLabel>
              <content src="chapter1.xhtml"/>
            </navPoint>
          </navMap>
        </ncx>
        """;

    private static string Chapter(string bodyHtml) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>Chapter 1</title></head>
          <body>{bodyHtml}</body>
        </html>
        """;
}
