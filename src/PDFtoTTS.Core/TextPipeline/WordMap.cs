namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// Bidirectional mapping between source word indices and normalized token
/// indices, derived from a token stream. Lets the merge step project a token's
/// timing back onto the source word(s) it came from, and vice versa
/// (design §2.4).
/// </summary>
public sealed class WordMap
{
    private readonly IReadOnlyList<NormToken> _tokens;
    private readonly Dictionary<int, List<int>> _sourceToTokens = new();

    public WordMap(IReadOnlyList<NormToken> tokens)
    {
        _tokens = tokens;
        foreach (var t in tokens)
        {
            for (int s = t.SourceStart; s <= t.SourceEnd; s++)
            {
                if (!_sourceToTokens.TryGetValue(s, out var list))
                    _sourceToTokens[s] = list = new List<int>();
                list.Add(t.Index);
            }
        }
    }

    /// <summary>Source word index range a token derives from.</summary>
    public (int Start, int End) SourceRangeForToken(int tokenIndex)
    {
        var t = _tokens[tokenIndex];
        return (t.SourceStart, t.SourceEnd);
    }

    /// <summary>Token indices that a given source word contributes to (in order).</summary>
    public IReadOnlyList<int> TokensForSourceWord(int sourceIndex) =>
        _sourceToTokens.TryGetValue(sourceIndex, out var list) ? list : Array.Empty<int>();

    /// <summary>All source word indices that have at least one token.</summary>
    public IReadOnlyCollection<int> CoveredSourceWords => _sourceToTokens.Keys;
}
