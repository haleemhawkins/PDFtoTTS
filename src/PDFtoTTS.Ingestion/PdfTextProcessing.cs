using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Ingestion;

/// <summary>A word as read from PdfPig, before global indexing.</summary>
public readonly record struct RawWord(string Text, int Page, BoundingBox Box);

/// <summary>
/// Pure text post-processing for PDF extraction: ligature normalization and
/// end-of-line hyphenation rejoining. Kept separate from <see cref="PdfExtractor"/>
/// so the tricky bits are unit-testable without a PDF.
/// </summary>
public static class PdfTextProcessing
{
    private static readonly Dictionary<char, string> Ligatures = new()
    {
        ['ﬀ'] = "ff",
        ['ﬁ'] = "fi",
        ['ﬂ'] = "fl",
        ['ﬃ'] = "ffi",
        ['ﬄ'] = "ffl",
        ['ﬅ'] = "st",
        ['ﬆ'] = "st"
    };

    /// <summary>Replace typographic ligature codepoints with canonical letters.</summary>
    public static string NormalizeLigatures(string text)
    {
        if (!text.Any(c => Ligatures.ContainsKey(c))) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(Ligatures.TryGetValue(c, out var rep) ? rep : c.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Rejoin words split by a hyphen at a line break. A word whose text ends in
    /// a hyphen (or soft hyphen) is merged with the following word only when that
    /// word is on a lower line of the same page — leaving same-line hyphenation
    /// (already a single PdfPig word in practice) untouched. The hyphen is
    /// dropped and the bounding boxes are unioned.
    /// </summary>
    public static IReadOnlyList<RawWord> RejoinHyphenation(IReadOnlyList<RawWord> words)
    {
        var result = new List<RawWord>(words.Count);
        int i = 0;
        while (i < words.Count)
        {
            var cur = words[i];
            while (EndsWithHyphen(cur.Text) && i + 1 < words.Count && IsLowerLineSamePage(cur, words[i + 1]))
            {
                var next = words[i + 1];
                cur = new RawWord(TrimHyphen(cur.Text) + next.Text, cur.Page, Union(cur.Box, next.Box));
                i++;
            }

            result.Add(cur);
            i++;
        }

        return result;
    }

    private static bool EndsWithHyphen(string s) => s.Length > 0 && s[^1] is '-' or '­';

    private static string TrimHyphen(string s) => s[..^1];

    // PDF user space has its origin at the bottom-left, so a lower line has a
    // smaller Bottom. A small tolerance avoids treating the same line as lower.
    private static bool IsLowerLineSamePage(RawWord cur, RawWord next) =>
        next.Page == cur.Page && next.Box.Y < cur.Box.Y - 1.0;

    private static BoundingBox Union(BoundingBox a, BoundingBox b)
    {
        double left = Math.Min(a.X, b.X);
        double bottom = Math.Min(a.Y, b.Y);
        double right = Math.Max(a.X + a.Width, b.X + b.Width);
        double top = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new BoundingBox(left, bottom, right - left, top - bottom);
    }
}
