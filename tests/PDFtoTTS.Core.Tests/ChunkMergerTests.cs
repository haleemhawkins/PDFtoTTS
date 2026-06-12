using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;
using PDFtoTTS.Orchestration;

namespace PDFtoTTS.Core.Tests;

public class ChunkMergerTests
{
    private static SourceWord Src(int i, string text) =>
        new(i, text, Page: 1, Bbox: new BoundingBox(0, 0, 10, 10));

    private static Chunk ChunkFrom(IReadOnlyList<SourceWord> src, IReadOnlyList<NormToken> tokens) =>
        new(0, string.Join(" ", tokens.Select(t => t.Text)),
            src[0].Index, src[^1].Index, 0, tokens);

    [Fact]
    public void Happy_path_assigns_each_word_its_aligned_window()
    {
        var src = new[] { Src(0, "hello"), Src(1, "world") };
        var tokens = new TextNormalizer().Normalize(src);
        var chunk = ChunkFrom(src, tokens);

        var alignment = new AlignmentResult(new[]
        {
            new AlignedWord("hello", 0, 500, 0.9f, false),
            new AlignedWord("world", 500, 1000, 0.9f, false),
        }, 1000);

        var result = new ChunkMerger().Merge(chunk, alignment, src, "/audio/0.wav");

        Assert.False(result.Degraded);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal((0, 500), (result.Words[0].StartMs, result.Words[0].EndMs));
        Assert.Equal((500, 1000), (result.Words[1].StartMs, result.Words[1].EndMs));
        Assert.Equal(1, result.Words[0].Page);
        Assert.NotNull(result.Words[0].Bbox);
    }

    [Fact]
    public void One_to_many_word_spans_union_of_token_windows()
    {
        // "1999" → three aligned tokens; the single source word's window spans all.
        var src = new[] { Src(0, "in"), Src(1, "1999") };
        var tokens = new TextNormalizer().Normalize(src);
        var chunk = ChunkFrom(src, tokens);

        var alignment = new AlignmentResult(new[]
        {
            new AlignedWord("in", 0, 200, 0.9f, false),
            new AlignedWord("nineteen", 200, 500, 0.9f, false),
            new AlignedWord("ninety", 500, 800, 0.9f, false),
            new AlignedWord("nine", 800, 1100, 0.9f, false),
        }, 1100);

        var result = new ChunkMerger().Merge(chunk, alignment, src, "/audio/0.wav");

        Assert.False(result.Degraded);
        var year = result.Words.Single(w => w.Index == 1);
        Assert.Equal(200, year.StartMs);   // first token start
        Assert.Equal(1100, year.EndMs);    // last token end
    }

    [Fact]
    public void Missing_alignment_is_interpolated_and_flags_degraded()
    {
        var src = new[] { Src(0, "a"), Src(1, "b"), Src(2, "c") };
        var tokens = new TextNormalizer().Normalize(src);
        var chunk = ChunkFrom(src, tokens);

        // "b" is missing from the alignment output.
        var alignment = new AlignmentResult(new[]
        {
            new AlignedWord("a", 0, 300, 0.9f, false),
            new AlignedWord("c", 600, 900, 0.9f, false),
        }, 900);

        var result = new ChunkMerger().Merge(chunk, alignment, src, "/audio/0.wav");

        Assert.True(result.Degraded);
        var b = result.Words.Single(w => w.Index == 1);
        Assert.Equal(300, b.StartMs); // interpolated between a.end and c.start
        Assert.Equal(600, b.EndMs);
    }

    [Fact]
    public void Low_confidence_alignment_flags_degraded_but_keeps_timing()
    {
        var src = new[] { Src(0, "hello"), Src(1, "world") };
        var tokens = new TextNormalizer().Normalize(src);
        var chunk = ChunkFrom(src, tokens);

        var alignment = new AlignmentResult(new[]
        {
            new AlignedWord("hello", 0, 500, 0.9f, false),
            new AlignedWord("world", 500, 1000, 0.1f, true), // low confidence
        }, 1000);

        var result = new ChunkMerger().Merge(chunk, alignment, src, "/audio/0.wav");

        Assert.True(result.Degraded);
        Assert.Equal((500, 1000), (result.Words[1].StartMs, result.Words[1].EndMs));
    }

    [Fact]
    public void Windows_are_monotonic_non_overlapping()
    {
        var src = new[] { Src(0, "a"), Src(1, "b") };
        var tokens = new TextNormalizer().Normalize(src);
        var chunk = ChunkFrom(src, tokens);

        // Overlapping windows from the aligner.
        var alignment = new AlignmentResult(new[]
        {
            new AlignedWord("a", 0, 600, 0.9f, false),
            new AlignedWord("b", 400, 900, 0.9f, false),
        }, 900);

        var result = new ChunkMerger().Merge(chunk, alignment, src, "/audio/0.wav");

        Assert.True(result.Words[1].StartMs >= result.Words[0].EndMs);
    }
}
