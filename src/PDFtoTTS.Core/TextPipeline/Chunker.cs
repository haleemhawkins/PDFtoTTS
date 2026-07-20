namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// One synthesizable chunk: a contiguous slice of the normalized token stream
/// within the token limit, tagged with the source word range and the character
/// offset of its text within the normalized document text.
/// </summary>
public sealed record Chunk(
    int Index,
    string Text,
    int SourceWordStart,
    int SourceWordEnd,
    int CharOffset,
    IReadOnlyList<NormToken> Tokens);

/// <summary>
/// Splits a normalized token stream into ordered chunks no larger than a token
/// limit, breaking at sentence boundaries where possible and never splitting a
/// single source word's expansion across two chunks (design §2.5).
/// </summary>
public sealed class Chunker
{
    private readonly int _maxTokens;
    private readonly int _firstChunkTokens;

    /// <param name="maxTokensPerChunk">Upper bound on tokens per chunk.</param>
    /// <param name="firstChunkTokens">
    /// Token limit for the FIRST chunk only. Defaults to the same limit; set it
    /// smaller so the first chunk synthesizes quickly and audio starts sooner
    /// (time-to-first-audio), while later chunks stay large to limit per-chunk
    /// overhead. Clamped to [1, maxTokensPerChunk].
    /// </param>
    public Chunker(int maxTokensPerChunk = 350, int? firstChunkTokens = null)
    {
        if (maxTokensPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTokensPerChunk));
        _maxTokens = maxTokensPerChunk;
        _firstChunkTokens = firstChunkTokens is int f
            ? Math.Clamp(f, 1, maxTokensPerChunk)
            : maxTokensPerChunk;
    }

    public IReadOnlyList<Chunk> Chunk(IReadOnlyList<NormToken> tokens)
    {
        var chunks = new List<Chunk>();
        int i = 0;
        int charOffset = 0;

        while (i < tokens.Count)
        {
            int limit = chunks.Count == 0 ? _firstChunkTokens : _maxTokens;
            int hardEnd = Math.Min(i + limit, tokens.Count);
            int breakAt = hardEnd;

            if (hardEnd < tokens.Count)
            {
                // Prefer the last sentence boundary inside the window.
                int lastBoundary = -1;
                for (int k = i; k < hardEnd; k++)
                    if (tokens[k].EndsSentence) lastBoundary = k;

                breakAt = lastBoundary >= i
                    ? lastBoundary + 1
                    : SourceWordBoundary(tokens, i, hardEnd);
            }

            var slice = tokens.Skip(i).Take(breakAt - i).ToList();
            // Re-attach sentence terminators stripped during normalization so the
            // voice gets period/question intonation and the worker inserts a pause
            // after each sentence (not just at chunk ends). '.' is the fallback when
            // a boundary is known but the exact mark wasn't recorded.
            string text = string.Join(" ", slice.Select(RenderToken));
            chunks.Add(new Chunk(
                Index: chunks.Count,
                Text: text,
                SourceWordStart: slice.Min(t => t.SourceStart),
                SourceWordEnd: slice.Max(t => t.SourceEnd),
                CharOffset: charOffset,
                Tokens: slice));

            charOffset += text.Length + 1; // +1 for the joining space between chunks
            i = breakAt;
        }

        return chunks;
    }

    // Token text with its sentence terminator re-attached. Commas/colons survive
    // normalization already; only '.'/'?'/'!' were peeled into the flag.
    private static string RenderToken(NormToken t)
    {
        if (!t.EndsSentence) return t.Text;
        char mark = t.Terminator == '\0' ? '.' : t.Terminator;
        return t.Text + mark;
    }

    /// <summary>
    /// Pull a hard break back so it lands on a source-word boundary — never
    /// between two tokens that expand the same source word.
    /// </summary>
    private static int SourceWordBoundary(IReadOnlyList<NormToken> tokens, int start, int hardEnd)
    {
        int breakAt = hardEnd;
        while (breakAt > start + 1 &&
               tokens[breakAt - 1].SourceStart == tokens[breakAt].SourceStart)
        {
            breakAt--;
        }

        return breakAt;
    }
}
