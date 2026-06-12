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

    public Chunker(int maxTokensPerChunk = 350)
    {
        if (maxTokensPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTokensPerChunk));
        _maxTokens = maxTokensPerChunk;
    }

    public IReadOnlyList<Chunk> Chunk(IReadOnlyList<NormToken> tokens)
    {
        var chunks = new List<Chunk>();
        int i = 0;
        int charOffset = 0;

        while (i < tokens.Count)
        {
            int hardEnd = Math.Min(i + _maxTokens, tokens.Count);
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
            string text = string.Join(" ", slice.Select(t => t.Text));
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
