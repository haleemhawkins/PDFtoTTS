using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PDFtoTTS.Core.TextPipeline;
using VersOne.Epub;
using VersOne.Epub.Options;

namespace PDFtoTTS.Ingestion;

/// <summary>
/// Extracts reading-order words from an EPUB using VersOne.Epub + AngleSharp
/// (design §2.2). EPUB is reflowable, so words carry no page/bbox; instead each
/// gets a <see cref="SourceWord.Locator"/> of "{spineHref}#{ordinal}".
///
/// Words are split <b>per text node</b> (whitespace within each text node), so a
/// word never spans inline elements. This makes the global word index exactly
/// reproducible in the browser by walking the rendered DOM the same way, which
/// is what drives EPUB highlight alignment (design §6.3).
/// </summary>
public sealed class EpubExtractor
{
    // Real-world EPUBs frequently violate the spec in several ways at once (a spine
    // item missing from the manifest, a manifest entry pointing at an absent file,
    // etc.). The STRICT default — and even RELAXED — throws on the first such error
    // and the book never opens. We only need the reading-order text, so suppress
    // all validation and salvage whatever content is present.
    private const EpubReaderOptionsPreset Options = EpubReaderOptionsPreset.IGNORE_ALL_ERRORS;

    public ExtractionResult Extract(string path) => ExtractCore(EpubReader.ReadBook(path, Options));

    public ExtractionResult Extract(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return ExtractCore(EpubReader.ReadBook(ms, Options));
    }

    private static ExtractionResult ExtractCore(EpubBook book)
    {
        var parser = new HtmlParser();
        var words = new List<SourceWord>();
        int index = 0;

        foreach (var file in book.ReadingOrder)
        {
            var doc = parser.ParseDocument(file.Content);
            if (doc.Body is null) continue;

            int ordinal = 0;
            foreach (var word in CollectWords(doc.Body))
            {
                words.Add(new SourceWord(
                    Index: index++,
                    Text: word,
                    Page: null,
                    Bbox: null,
                    Locator: $"{file.FilePath}#{ordinal++}"));
            }
        }

        return new ExtractionResult(words, book.ReadingOrder.Count);
    }

    // Walk text nodes in document order; split each on whitespace. Markup is
    // skipped (text only) and AngleSharp has already decoded HTML entities.
    private static IEnumerable<string> CollectWords(INode node)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                foreach (var w in child.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    yield return w;
            }
            else if (child is IElement element)
            {
                foreach (var w in CollectWords(element))
                    yield return w;
            }
        }
    }
}
