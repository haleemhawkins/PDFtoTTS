using PDFtoTTS.Core.Tests.Fixtures;
using PDFtoTTS.Ingestion;

namespace PDFtoTTS.Core.Tests;

public class EpubExtractorTests
{
    private static ExtractionResult Extract(string bodyHtml) =>
        new EpubExtractor().Extract(EpubFixtureBuilder.Build(bodyHtml));

    [Fact]
    public void Extracts_words_in_reading_order_with_markup_stripped()
    {
        var result = Extract("<p>Hello <em>brave</em> new world.</p><p>Second paragraph.</p>");

        Assert.Equal(
            new[] { "Hello", "brave", "new", "world.", "Second", "paragraph." },
            result.Words.Select(w => w.Text));

        // No markup leaks into token text.
        Assert.DoesNotContain(result.Words, w => w.Text.Contains('<'));
    }

    [Fact]
    public void Epub_words_have_no_page_but_carry_a_locator()
    {
        var result = Extract("<p>One two three.</p>");

        Assert.All(result.Words, w =>
        {
            Assert.Null(w.Page);
            Assert.Null(w.Bbox);
            Assert.NotNull(w.Locator);
            Assert.Contains("chapter1", w.Locator!);
        });

        // Locator ordinals are per-file and ascending.
        Assert.EndsWith("#0", result.Words[0].Locator);
        Assert.EndsWith("#1", result.Words[1].Locator);
    }

    [Fact]
    public void Html_entities_are_decoded()
    {
        var result = Extract("<p>Second &amp; paragraph.</p>");

        Assert.Contains("&", result.Words.Select(w => w.Text));
        Assert.DoesNotContain(result.Words, w => w.Text.Contains("amp"));
    }

    [Fact]
    public void Words_are_split_per_text_node()
    {
        // Per-text-node splitting: each inline element's text is its own word(s),
        // so indices are reproducible by walking the rendered DOM the same way.
        var result = Extract("<p>foo<em>bar</em>baz qux</p>");

        Assert.Equal(new[] { "foo", "bar", "baz", "qux" }, result.Words.Select(w => w.Text));
    }
}
