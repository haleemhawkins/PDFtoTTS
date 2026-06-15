using PDFtoTTS.Core.Models;
using PDFtoTTS.Ingestion;

namespace PDFtoTTS.Core.Tests;

public class PdfTextProcessingTests
{
    [Theory]
    [InlineData("ﬁle", "file")]
    [InlineData("oﬃce", "office")]
    [InlineData("ﬂour", "flour")]
    [InlineData("plain", "plain")]
    public void NormalizeLigatures_canonicalizes(string input, string expected) =>
        Assert.Equal(expected, PdfTextProcessing.NormalizeLigatures(input));

    [Fact]
    public void Hyphenated_line_break_is_rejoined()
    {
        // "exam-" on a higher line, "ple" on the next line below it.
        var words = new[]
        {
            new RawWord("exam-", 1, new BoundingBox(50, 100, 30, 10)),
            new RawWord("ple", 1, new BoundingBox(50, 88, 20, 10)),
        };

        var merged = PdfTextProcessing.RejoinHyphenation(words);

        Assert.Single(merged);
        Assert.Equal("example", merged[0].Text);
        // Box unions both fragments: bottom 88, top 110 → height 22.
        Assert.Equal(88, merged[0].Box.Y);
        Assert.Equal(22, merged[0].Box.Height);
    }

    [Fact]
    public void Same_line_hyphen_is_not_rejoined()
    {
        var words = new[]
        {
            new RawWord("co-", 1, new BoundingBox(50, 100, 20, 10)),
            new RawWord("op", 1, new BoundingBox(70, 100, 20, 10)), // same line
        };

        var merged = PdfTextProcessing.RejoinHyphenation(words);

        Assert.Equal(2, merged.Count);
    }

    // --- running header/footer detection ------------------------------------

    private const double PageH = 800; // top band ≥720, bottom band ≤80 (10%)

    private static PageLine Line(int page, double centerY, params string[] words)
    {
        var raw = words.Select((w, i) =>
            new RawWord(w, page, new BoundingBox(50 + i * 30, centerY - 5, 25, 10))).ToList();
        return new PageLine(page, raw);
    }

    [Fact]
    public void Strips_recurring_header_and_page_numbers_but_keeps_body()
    {
        var lines = new List<PageLine>();
        var heights = new Dictionary<int, double>();
        for (int p = 1; p <= 5; p++)
        {
            heights[p] = PageH;
            lines.Add(Line(p, 760, "The", "Great", "Gatsby"));      // running header (top band)
            lines.Add(Line(p, 400, "body", "text", "on", $"p{p}")); // body (middle)
            lines.Add(Line(p, 40, p.ToString()));                   // page number (bottom band)
        }

        var kept = PdfTextProcessing.StripRunningHeadersFooters(lines, heights)
            .Select(w => w.Text).ToList();

        Assert.DoesNotContain("Gatsby", kept);            // running header gone
        Assert.DoesNotContain("3", kept);                 // page numbers gone
        Assert.Equal(20, kept.Count);                     // 4 body words × 5 pages
        Assert.All(new[] { "body", "text", "on" }, w => Assert.Contains(w, kept));
    }

    [Fact]
    public void Keeps_a_unique_heading_in_the_margin_band()
    {
        // A chapter-only page: the heading sits high (top band) but appears once,
        // so it is NOT boilerplate and must still be read.
        var lines = new List<PageLine>();
        var heights = new Dictionary<int, double>();
        for (int p = 1; p <= 4; p++)
        {
            heights[p] = PageH;
            lines.Add(Line(p, 760, "Running", "Title")); // recurs → stripped
            lines.Add(Line(p, 400, "body", $"{p}"));
        }
        heights[5] = PageH;
        lines.Add(Line(5, 760, "Chapter", "Five")); // unique heading high on the page

        var kept = PdfTextProcessing.StripRunningHeadersFooters(lines, heights)
            .Select(w => w.Text).ToList();

        Assert.DoesNotContain("Running", kept);
        Assert.Contains("Chapter", kept);
        Assert.Contains("Five", kept);
    }

    [Fact]
    public void Short_documents_keep_headers_but_still_drop_page_numbers()
    {
        // Under 3 pages there isn't enough signal to call a line a running header,
        // but a bare page number in the margin is always boilerplate.
        var lines = new List<PageLine>
        {
            Line(1, 760, "Header"), Line(1, 400, "body"), Line(1, 40, "1"),
            Line(2, 760, "Header"), Line(2, 400, "body"), Line(2, 40, "2"),
        };
        var heights = new Dictionary<int, double> { [1] = PageH, [2] = PageH };

        var kept = PdfTextProcessing.StripRunningHeadersFooters(lines, heights)
            .Select(w => w.Text).ToList();

        Assert.Equal(new[] { "Header", "body", "Header", "body" }, kept);
    }

    [Theory]
    [InlineData("42")]          // bare arabic
    [InlineData("Page 42")]     // labelled
    [InlineData("— 42 —")]      // dash-wrapped
    [InlineData("xiv")]         // roman (front matter)
    [InlineData("p. 7")]        // abbreviated label
    public void Bare_page_numbers_in_the_margin_are_dropped(string pageNo)
    {
        var lines = new List<PageLine>
        {
            Line(1, 400, "real", "body", "content"),
            Line(1, 40, pageNo.Split(' ')), // footer band
        };
        var heights = new Dictionary<int, double> { [1] = PageH };

        var kept = PdfTextProcessing.StripRunningHeadersFooters(lines, heights)
            .Select(w => w.Text).ToList();

        Assert.Equal(new[] { "real", "body", "content" }, kept);
    }

    [Fact]
    public void Roman_lookalike_words_are_not_treated_as_page_numbers()
    {
        // "did" and "mill" are spelled from roman letters but aren't valid numerals;
        // a numeric-looking word in the body must never be dropped.
        var lines = new List<PageLine>
        {
            Line(1, 40, "did"),   // margin band, but a real word
            Line(1, 400, "mill", "town"),
        };
        var heights = new Dictionary<int, double> { [1] = PageH };

        var kept = PdfTextProcessing.StripRunningHeadersFooters(lines, heights)
            .Select(w => w.Text).ToList();

        Assert.Equal(new[] { "did", "mill", "town" }, kept);
    }
}
