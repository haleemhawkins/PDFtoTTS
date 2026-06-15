using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace PDFtoTTS.Ingestion;

/// <summary>
/// Extracts reading-order words with bounding boxes from a PDF using PdfPig
/// (design §2.1). Words are grouped into blocks (Docstrum) and ordered with the
/// unsupervised reading-order detector so multi-column layouts read column by
/// column rather than line-straddling. Ligatures are normalized and end-of-line
/// hyphenation rejoined before words are assigned a global document index.
/// </summary>
public sealed class PdfExtractor
{
    private readonly UnsupervisedReadingOrderDetector _readingOrder = new(10);

    public ExtractionResult Extract(string path)
    {
        using var doc = PdfDocument.Open(path);
        return ExtractCore(doc);
    }

    public ExtractionResult Extract(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        return ExtractCore(doc);
    }

    private ExtractionResult ExtractCore(PdfDocument doc)
    {
        // Collect words grouped into reading-order lines (per page) so running
        // headers/footers can be detected and dropped before global indexing.
        var lines = new List<PageLine>();
        var pageHeights = new Dictionary<int, double>();
        foreach (var page in doc.GetPages())
        {
            pageHeights[page.Number] = page.Height;
            foreach (var lineWords in LinesInReadingOrder(page))
            {
                var raw = new List<RawWord>(lineWords.Count);
                foreach (var word in lineWords)
                {
                    string text = PdfTextProcessing.NormalizeLigatures(word.Text);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var b = word.BoundingBox;
                    raw.Add(new RawWord(text, page.Number, new BoundingBox(b.Left, b.Bottom, b.Width, b.Height)));
                }
                if (raw.Count > 0) lines.Add(new PageLine(page.Number, raw));
            }
        }

        // Strip running headers/footers/page numbers, then rejoin hyphenation over
        // the surviving body words (so a stripped header can't merge into the body).
        var body = PdfTextProcessing.StripRunningHeadersFooters(lines, pageHeights);
        var merged = PdfTextProcessing.RejoinHyphenation(body);
        var words = new List<SourceWord>(merged.Count);
        for (int i = 0; i < merged.Count; i++)
            words.Add(new SourceWord(i, merged[i].Text, merged[i].Page, merged[i].Box));

        return new ExtractionResult(words, doc.NumberOfPages);
    }

    // Segment the page into text blocks and order them by reading order, yielding
    // each line's words. Falls back to one word per line if the page has no
    // detectable blocks (e.g. a single scattered word).
    private IEnumerable<IReadOnlyList<Word>> LinesInReadingOrder(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        if (blocks.Count == 0)
        {
            foreach (var w in words) yield return new[] { w };
            yield break;
        }

        foreach (var block in _readingOrder.Get(blocks))
            foreach (var line in block.TextLines)
                yield return line.Words;
    }
}
