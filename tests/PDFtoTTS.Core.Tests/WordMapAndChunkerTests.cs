using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Core.Tests;

public class WordMapAndChunkerTests
{
    private readonly TextNormalizer _norm = new();

    [Fact]
    public void One_source_word_maps_to_all_its_tokens()
    {
        // "1999" expands to three tokens, all mapping back to source word 1.
        var tokens = _norm.Normalize(new[]
        {
            new SourceWord(0, "in"),
            new SourceWord(1, "1999"),
        });
        var map = new WordMap(tokens);

        var tokenIdx = map.TokensForSourceWord(1);
        Assert.Equal(3, tokenIdx.Count);
        Assert.All(tokenIdx, i => Assert.Equal((1, 1), map.SourceRangeForToken(i)));
    }

    [Fact]
    public void Map_covers_every_source_word_that_produced_a_token()
    {
        var tokens = _norm.Normalize(new[]
        {
            new SourceWord(0, "the"),
            new SourceWord(1, "21st"),
            new SourceWord(2, "time"),
        });
        var map = new WordMap(tokens);

        Assert.Equal(new[] { 0, 1, 2 }, map.CoveredSourceWords.OrderBy(x => x));
    }

    [Fact]
    public void Chunks_are_contiguous_and_cover_all_tokens_in_order()
    {
        var tokens = Tokens(60); // 60 single-token sentences
        var chunks = new Chunker(maxTokensPerChunk: 25).Chunk(tokens);

        // Contiguous indices from 0.
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));

        // Concatenation of chunk tokens == original stream, in order.
        var flattened = chunks.SelectMany(c => c.Tokens).Select(t => t.Index).ToList();
        Assert.Equal(Enumerable.Range(0, tokens.Count), flattened);
    }

    [Fact]
    public void No_chunk_exceeds_the_token_limit()
    {
        var tokens = Tokens(200);
        var chunks = new Chunker(maxTokensPerChunk: 30).Chunk(tokens);
        Assert.All(chunks, c => Assert.True(c.Tokens.Count <= 30));
    }

    [Fact]
    public void Chunks_prefer_sentence_boundaries()
    {
        // Two sentences of 10 tokens each; limit 15 must break after sentence 1.
        var tokens = SentenceTokens(10).Concat(SentenceTokens(10, startIndex: 10, sourceOffset: 10))
            .Select((t, i) => t with { Index = i }).ToList();

        var chunks = new Chunker(maxTokensPerChunk: 15).Chunk(tokens);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(10, chunks[0].Tokens.Count); // broke at the sentence boundary, not at 15
        Assert.True(chunks[0].Tokens[^1].EndsSentence);
    }

    [Fact]
    public void Hard_break_never_splits_a_source_words_expansion()
    {
        // Each source word expands to 3 tokens, no sentence boundaries.
        // A limit of 4 must pull back to a 3-token boundary, not split a word.
        var tokens = new List<NormToken>();
        for (int src = 0; src < 6; src++)
            for (int j = 0; j < 3; j++)
                tokens.Add(new NormToken(tokens.Count, $"w{src}_{j}", src, src));

        var chunks = new Chunker(maxTokensPerChunk: 4).Chunk(tokens);

        foreach (var c in chunks)
        {
            // No source word appears split across this and another chunk:
            // each chunk's token count is a multiple of 3 (whole words only).
            Assert.Equal(0, c.Tokens.Count % 3);
        }
    }

    private static List<NormToken> Tokens(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new NormToken(i, $"t{i}", i, i, EndsSentence: true))
            .ToList();

    private static List<NormToken> SentenceTokens(int n, int startIndex = 0, int sourceOffset = 0) =>
        Enumerable.Range(0, n)
            .Select(i => new NormToken(
                startIndex + i, $"t{startIndex + i}", sourceOffset + i, sourceOffset + i,
                EndsSentence: i == n - 1))
            .ToList();
}
