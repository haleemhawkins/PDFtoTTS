using PDFtoTTS.Ingestion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PDFtoTTS.Core.Tests;

public class PdfExtractorTests
{
    static PdfExtractorTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static byte[] MakePdf(string text) =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.Content().Text(text);
        })).GeneratePdf();

    [Fact]
    public void Extracts_words_with_page_and_bounding_boxes()
    {
        var pdf = MakePdf("Hello world from PdfPig");

        var result = new PdfExtractor().Extract(pdf);

        Assert.Equal(1, result.PageCount);
        Assert.Equal(new[] { "Hello", "world", "from", "PdfPig" },
            result.Words.Select(w => w.Text));

        // Global indices are contiguous from zero.
        Assert.Equal(Enumerable.Range(0, 4), result.Words.Select(w => w.Index));

        Assert.All(result.Words, w =>
        {
            Assert.Equal(1, w.Page);
            Assert.NotNull(w.Bbox);
            Assert.True(w.Bbox!.Value.Width > 0);
            Assert.True(w.Bbox!.Value.Height > 0);
        });
    }

    private static byte[] MakeTwoColumnPdf() =>
        Document.Create(c => c.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.Content().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("alpha");
                    col.Item().Text("bravo");
                    col.Item().Text("charlie");
                });
                row.ConstantItem(60); // clear gutter between the two columns
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("one");
                    col.Item().Text("two");
                    col.Item().Text("three");
                });
            });
        })).GeneratePdf();

    [Fact]
    public void Reads_columns_in_reading_order_not_across_them()
    {
        var result = new PdfExtractor().Extract(MakeTwoColumnPdf());
        var order = result.Words.Select(w => w.Text).ToList();

        // The entire left column must be read before the right column — never
        // line-straddling (which would interleave to alpha, one, bravo, ...).
        int lastLeft = Math.Max(order.IndexOf("alpha"), Math.Max(order.IndexOf("bravo"), order.IndexOf("charlie")));
        int firstRight = Math.Min(order.IndexOf("one"), Math.Min(order.IndexOf("two"), order.IndexOf("three")));

        Assert.True(lastLeft < firstRight,
            $"expected left column before right; got: {string.Join(", ", order)}");
    }
}
