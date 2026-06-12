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
        var raw = new List<RawWord>();
        foreach (var page in doc.GetPages())
        {
            foreach (var word in WordsInReadingOrder(page))
            {
                string text = PdfTextProcessing.NormalizeLigatures(word.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;
                var b = word.BoundingBox;
                raw.Add(new RawWord(text, page.Number, new BoundingBox(b.Left, b.Bottom, b.Width, b.Height)));
            }
        }

        var merged = PdfTextProcessing.RejoinHyphenation(raw);
        var words = new List<SourceWord>(merged.Count);
        for (int i = 0; i < merged.Count; i++)
            words.Add(new SourceWord(i, merged[i].Text, merged[i].Page, merged[i].Box));

        return new ExtractionResult(words, doc.NumberOfPages);
    }

    // Segment the page into text blocks and order them by reading order, then
    // walk lines/words within each block. Falls back to raw word order if the
    // page has no detectable blocks (e.g. a single scattered word).
    private IEnumerable<Word> WordsInReadingOrder(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        if (blocks.Count == 0)
        {
            foreach (var w in words) yield return w;
            yield break;
        }

        foreach (var block in _readingOrder.Get(blocks))
            foreach (var line in block.TextLines)
                foreach (var word in line.Words)
                    yield return word;
    }
}
