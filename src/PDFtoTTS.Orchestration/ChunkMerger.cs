using PDFtoTTS.Core.Models;
using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Orchestration;

/// <summary>
/// Merges forced-alignment word timings with source-word bounding boxes to
/// produce the per-source-word <see cref="WordData"/> the reader highlights
/// (design §5.3). Aligned tokens are matched to the chunk's expected spoken
/// words (with fuzzy fallback), each chunk token's window is projected onto the
/// source word(s) it came from, and any source word left without a match is
/// interpolated from its neighbours. Chunks needing interpolation or with a
/// token-count mismatch are flagged <see cref="ProcessedChunk.Degraded"/>.
/// </summary>
public sealed class ChunkMerger
{
    private readonly float _lowConfidenceThreshold;

    public ChunkMerger(float lowConfidenceThreshold = 0.30f) =>
        _lowConfidenceThreshold = lowConfidenceThreshold;

    public ProcessedChunk Merge(
        Chunk chunk,
        AlignmentResult alignment,
        IReadOnlyList<SourceWord> sourceWords,
        string audioUrl)
    {
        var tokens = chunk.Tokens;

        // Flatten chunk tokens into spoken words, remembering each one's owning
        // token (a token like "New York" contributes two spoken words).
        var expectedWords = new List<string>();
        var expectedOwner = new List<int>(); // index into `tokens`
        for (int li = 0; li < tokens.Count; li++)
            foreach (var w in tokens[li].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                expectedWords.Add(w);
                expectedOwner.Add(li);
            }

        int[] expectedToAligned = AlignSequences(expectedWords, alignment.Words);

        // Aggregate matched alignment windows per chunk token.
        var tokenWindow = new (long Start, long End, float Conf, bool Matched)[tokens.Count];
        for (int li = 0; li < tokens.Count; li++) tokenWindow[li] = (0, 0, 1f, false);

        bool anyLowConfidence = false;
        for (int e = 0; e < expectedWords.Count; e++)
        {
            int a = expectedToAligned[e];
            if (a < 0) continue;
            var aw = alignment.Words[a];
            if (aw.LowConfidence || aw.Confidence < _lowConfidenceThreshold) anyLowConfidence = true;

            int li = expectedOwner[e];
            ref var win = ref tokenWindow[li];
            win = win.Matched
                ? (Math.Min(win.Start, aw.StartMs), Math.Max(win.End, aw.EndMs), Math.Min(win.Conf, aw.Confidence), true)
                : (aw.StartMs, aw.EndMs, aw.Confidence, true);
        }

        // Project token windows onto source words.
        var words = new List<WordData>();
        bool interpolated = false;
        for (int s = chunk.SourceWordStart; s <= chunk.SourceWordEnd; s++)
        {
            var owning = new List<int>();
            for (int li = 0; li < tokens.Count; li++)
                if (tokens[li].SourceStart <= s && s <= tokens[li].SourceEnd && tokenWindow[li].Matched)
                    owning.Add(li);

            var src = sourceWords[s];
            if (owning.Count > 0)
            {
                long start = owning.Min(li => tokenWindow[li].Start);
                long end = owning.Max(li => tokenWindow[li].End);
                float conf = owning.Min(li => tokenWindow[li].Conf);
                words.Add(new WordData(s, src.Text, start, end, src.Page, src.Bbox, conf));
            }
            else
            {
                interpolated = true;
                // -1 marks "needs interpolation"; filled in InterpolateGaps.
                words.Add(new WordData(s, src.Text, -1, -1, src.Page, src.Bbox, 0f));
            }
        }

        InterpolateGaps(words, alignment.AudioDurationMs);
        EnforceMonotonic(words);

        bool degraded = interpolated || anyLowConfidence || alignment.Words.Count != expectedWords.Count;
        return new ProcessedChunk(chunk.Index, audioUrl, alignment.AudioDurationMs, words, degraded);
    }

    /// <summary>
    /// Align expected spoken words to aligned words via LCS over fuzzy-equal
    /// tokens. Returns, for each expected index, the matched aligned index or -1.
    /// The common case (transcript == what was aligned) pairs everything 1:1.
    /// </summary>
    private static int[] AlignSequences(IReadOnlyList<string> expected, IReadOnlyList<AlignedWord> aligned)
    {
        int n = expected.Count, m = aligned.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = FuzzyEqual(expected[i], aligned[j].Text)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new int[n];
        Array.Fill(result, -1);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (FuzzyEqual(expected[x], aligned[y].Text)) { result[x] = y; x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
            else y++;
        }

        return result;
    }

    private static bool FuzzyEqual(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return true;
        int dist = Levenshtein(a, b);
        int max = Math.Max(a.Length, b.Length);
        return max > 0 && 1.0 - (double)dist / max >= 0.8;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }

        return d[a.Length, b.Length];
    }

    // Fill timing for words that had no alignment match by linear interpolation
    // between the previous matched end and the next matched start.
    private static void InterpolateGaps(List<WordData> words, long audioDurationMs)
    {
        int i = 0;
        while (i < words.Count)
        {
            if (words[i].StartMs >= 0) { i++; continue; }

            int gapStart = i;
            while (i < words.Count && words[i].StartMs < 0) i++;
            int gapEnd = i; // exclusive

            long left = gapStart > 0 ? words[gapStart - 1].EndMs : 0;
            long right = gapEnd < words.Count ? words[gapEnd].StartMs : audioDurationMs;
            if (right < left) right = left;

            int count = gapEnd - gapStart;
            long span = right - left;
            for (int k = 0; k < count; k++)
            {
                long start = left + span * k / count;
                long end = left + span * (k + 1) / count;
                words[gapStart + k] = words[gapStart + k] with { StartMs = start, EndMs = end };
            }
        }
    }

    // Clamp any overlaps so windows are non-decreasing and never negative.
    private static void EnforceMonotonic(List<WordData> words)
    {
        long prevEnd = 0;
        for (int i = 0; i < words.Count; i++)
        {
            long start = Math.Max(words[i].StartMs, prevEnd);
            long end = Math.Max(words[i].EndMs, start);
            words[i] = words[i] with { StartMs = start, EndMs = end };
            prevEnd = end;
        }
    }
}
