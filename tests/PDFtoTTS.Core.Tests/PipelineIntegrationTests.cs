using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Ingestion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PDFtoTTS.Core.Tests;

/// <summary>
/// Checkpoint A (design tasks §2): a PDF flows through extraction → normalization
/// → mapping → chunking with no GPU, yielding ordered chunks whose tokens map
/// back to the extracted source words.
/// </summary>
public class PipelineIntegrationTests
{
    static PipelineIntegrationTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static byte[] MakePdf(string text) =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.Content().Text(text);
        })).GeneratePdf();

    [Fact]
    public void Pdf_to_chunks_with_word_map_no_gpu()
    {
        // "1999" exercises the 1→N expansion through the whole pipeline.
        var pdf = MakePdf("The meeting was in 1999 and it cost $5 today.");

        var extracted = new PdfExtractor().Extract(pdf);
        var tokens = new TextNormalizer().Normalize(extracted.Words);
        var map = new WordMap(tokens);
        var chunks = new Chunker(maxTokensPerChunk: 50).Chunk(tokens);

        // The year expanded to three tokens, all mapping back to one source word.
        Assert.Contains("nineteen", tokens.Select(t => t.Text));
        var yearWordIndex = extracted.Words.Single(w => w.Text == "1999").Index;
        Assert.Equal(3, map.TokensForSourceWord(yearWordIndex).Count);

        // Currency expanded too.
        Assert.Contains("dollars", tokens.Select(t => t.Text));

        // Chunks cover every token in order with nothing dropped.
        var flattened = chunks.SelectMany(c => c.Tokens).Select(t => t.Index).ToList();
        Assert.Equal(Enumerable.Range(0, tokens.Count), flattened);

        // Every chunk token resolves to a real extracted source word with a bbox.
        foreach (var token in chunks.SelectMany(c => c.Tokens))
        {
            var src = extracted.Words[token.SourceStart];
            Assert.Equal(1, src.Page);
            Assert.NotNull(src.Bbox);
        }
    }
}
