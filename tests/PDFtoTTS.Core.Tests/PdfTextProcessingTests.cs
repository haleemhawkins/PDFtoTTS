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
}
