using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Core.Tests;

public class TextNormalizerTests
{
    private readonly TextNormalizer _norm = new();

    private string Classify(string word) =>
        string.Join(" ", _norm.ClassifyWord(word).Tokens);

    [Theory]
    [InlineData("1999", "nineteen ninety nine")]
    [InlineData("1,250", "one thousand two hundred fifty")]
    [InlineData("3.14", "three point one four")]
    [InlineData("3rd", "third")]
    [InlineData("21st", "twenty first")]
    [InlineData("50%", "fifty percent")]
    [InlineData("$1,250.50", "one thousand two hundred fifty dollars and fifty cents")]
    [InlineData("$1", "one dollar")]
    [InlineData("5kg", "five kilograms")]
    [InlineData("10km", "ten kilometers")]
    public void Numeric_forms_expand(string word, string expected) =>
        Assert.Equal(expected, Classify(word));

    [Theory]
    [InlineData("Dr.", "Doctor")]
    [InlineData("e.g.", "for example")]
    [InlineData("i.e.", "that is")]
    [InlineData("etc.", "et cetera")]
    public void Abbreviations_expand(string word, string expected) =>
        Assert.Equal(expected, Classify(word));

    [Fact]
    public void Abbreviation_period_is_not_a_sentence_boundary()
    {
        var (_, endsSentence) = _norm.ClassifyWord("Dr.");
        Assert.False(endsSentence);
    }

    [Fact]
    public void Acronym_stays_one_token()
    {
        var (tokens, _) = _norm.ClassifyWord("NASA");
        Assert.Equal(new[] { "NASA" }, tokens);
    }

    [Theory]
    [InlineData("https://example.com/docs", "example dot com slash docs")]
    [InlineData("www.example.org", "example dot org")]
    [InlineData("a@b.com", "a at b dot com")]
    public void Urls_and_emails_are_spoken(string word, string expected) =>
        Assert.Equal(expected, Classify(word));

    [Fact]
    public void Sentence_terminator_sets_flag_and_is_dropped()
    {
        var (tokens, endsSentence) = _norm.ClassifyWord("time.");
        Assert.Equal(new[] { "time" }, tokens);
        Assert.True(endsSentence);
    }

    [Fact]
    public void Each_token_maps_back_to_its_source_word_index()
    {
        // "in 1999 ." → "in" (src 0), "nineteen"/"ninety"/"nine" (src 1)
        var words = new[]
        {
            new SourceWord(0, "in"),
            new SourceWord(1, "1999"),
        };

        var tokens = _norm.Normalize(words);

        Assert.Equal("in nineteen ninety nine", string.Join(" ", tokens.Select(t => t.Text)));
        Assert.Equal(0, tokens[0].SourceStart);
        Assert.All(tokens.Skip(1), t => Assert.Equal(1, t.SourceStart));
    }

    [Fact]
    public void Multiword_phrase_collapses_to_one_token_spanning_source_words()
    {
        // N→1: "New York" (source words 2 and 3) becomes a single token.
        var words = new[]
        {
            new SourceWord(0, "I"),
            new SourceWord(1, "love"),
            new SourceWord(2, "New"),
            new SourceWord(3, "York."),
        };

        var tokens = _norm.Normalize(words);

        var ny = Assert.Single(tokens, t => t.Text == "New York");
        Assert.Equal(2, ny.SourceStart);
        Assert.Equal(3, ny.SourceEnd);
        Assert.True(ny.EndsSentence); // terminator on "York." carried through

        // Both source words map back to the single phrase token.
        var map = new WordMap(tokens);
        Assert.Equal(new[] { ny.Index }, map.TokensForSourceWord(2));
        Assert.Equal(new[] { ny.Index }, map.TokensForSourceWord(3));
    }

    [Fact]
    public void Phrase_collapsing_can_be_disabled()
    {
        var norm = new TextNormalizer(phrases: Array.Empty<string>());
        var tokens = norm.Normalize(new[]
        {
            new SourceWord(0, "New"),
            new SourceWord(1, "York"),
        });

        Assert.Equal(new[] { "New", "York" }, tokens.Select(t => t.Text));
    }

    [Fact]
    public void Standalone_terminator_promotes_previous_token()
    {
        var words = new[]
        {
            new SourceWord(0, "hello"),
            new SourceWord(1, "."),
        };

        var tokens = _norm.Normalize(words);

        Assert.Single(tokens);
        Assert.True(tokens[0].EndsSentence);
    }
}
