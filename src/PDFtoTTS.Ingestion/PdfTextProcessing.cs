using System.Text;
using System.Text.RegularExpressions;
using PDFtoTTS.Core.Models;

namespace PDFtoTTS.Ingestion;

/// <summary>A word as read from PdfPig, before global indexing.</summary>
public readonly record struct RawWord(string Text, int Page, BoundingBox Box);

/// <summary>A line of words on a page, in reading order, used for running
/// header/footer detection (which is a per-line, not per-word, decision).</summary>
public readonly record struct PageLine(int Page, IReadOnlyList<RawWord> Words);

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

    /// <summary>
    /// Drop running headers, footers, and page numbers so the TTS reads like a
    /// human would. The discriminator is recurrence, NOT "looks like a heading":
    /// a line in a page's top/bottom margin band that REPEATS across many pages
    /// (a running chapter title, a page number) is boilerplate and removed; a line
    /// that appears once — a real chapter heading, including a chapter-only page —
    /// is kept and read. Page numbers vary per page, so signatures collapse digit
    /// runs to '#' ("Page 12"/"Page 13" → "page #", "12"/"13" → "#") to recur.
    /// Returns the body words, in reading order, with boilerplate lines removed.
    /// </summary>
    /// <param name="bandFraction">Fraction of page height treated as the top/bottom
    /// margin band where headers/footers live (default 10%).</param>
    public static IReadOnlyList<RawWord> StripRunningHeadersFooters(
        IReadOnlyList<PageLine> lines,
        IReadOnlyDictionary<int, double> pageHeights,
        double bandFraction = 0.10)
    {
        int pageCount = pageHeights.Count;

        // Recurring header/footer signatures — only meaningful with ≥3 pages, where a
        // line that repeats across the margins is boilerplate rather than a one-off.
        var boilerplate = pageCount >= 3
            ? RecurringMarginSignatures(lines, pageHeights, bandFraction, pageCount)
            : new HashSet<string>();

        // Drop margin-band lines that are either a recurring header/footer OR a bare
        // page number (stripped regardless of recurrence — a lone number/roman numeral
        // in the margin is never body text). Everything else is kept in reading order.
        var kept = new List<RawWord>();
        foreach (var line in lines)
        {
            if (InMarginBand(line, pageHeights, bandFraction)
                && (IsPageNumber(line) || boilerplate.Contains(Signature(line))))
                continue;
            kept.AddRange(line.Words);
        }
        return kept;
    }

    // Margin signatures that recur across ≥25% of pages (floor of 3): running
    // headers/footers, as opposed to a unique heading that appears on a single page.
    private static HashSet<string> RecurringMarginSignatures(
        IReadOnlyList<PageLine> lines, IReadOnlyDictionary<int, double> pageHeights,
        double bandFraction, int pageCount)
    {
        var pagesBySignature = new Dictionary<string, HashSet<int>>();
        foreach (var line in lines)
        {
            if (!InMarginBand(line, pageHeights, bandFraction)) continue;
            string sig = Signature(line);
            if (sig.Length == 0) continue;
            if (!pagesBySignature.TryGetValue(sig, out var pages))
                pagesBySignature[sig] = pages = new HashSet<int>();
            pages.Add(line.Page);
        }

        double minPages = Math.Max(3, pageCount * 0.25);
        return pagesBySignature
            .Where(kv => kv.Value.Count >= minPages)
            .Select(kv => kv.Key)
            .ToHashSet();
    }

    // A line sits in a margin band when its vertical centre is within bandFraction
    // of the page top or bottom. PDF user space is bottom-origin, so the top band is
    // near pageHeight and the bottom band near 0.
    private static bool InMarginBand(PageLine line, IReadOnlyDictionary<int, double> pageHeights, double bandFraction)
    {
        if (line.Words.Count == 0) return false;
        if (!pageHeights.TryGetValue(line.Page, out double h) || h <= 0) return false;
        double cy = LineCenterY(line);
        double band = h * bandFraction;
        return cy >= h - band || cy <= band;
    }

    private static double LineCenterY(PageLine line)
    {
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var w in line.Words)
        {
            minY = Math.Min(minY, w.Box.Y);
            maxY = Math.Max(maxY, w.Box.Y + w.Box.Height);
        }
        return (minY + maxY) / 2;
    }

    // Position/number-independent template for a line: lowercased, whitespace
    // collapsed, and runs of digits replaced by '#' so per-page page numbers and
    // numbered running headers still collapse to one recurring signature.
    private static string Signature(PageLine line)
    {
        var raw = new StringBuilder();
        foreach (var w in line.Words)
        {
            if (raw.Length > 0) raw.Append(' ');
            raw.Append(w.Text);
        }

        var sig = new StringBuilder(raw.Length);
        bool inDigit = false, lastSpace = true; // leading-space suppressed
        foreach (char c in raw.ToString().ToLowerInvariant())
        {
            if (char.IsDigit(c))
            {
                if (!inDigit) sig.Append('#');
                inDigit = true;
                lastSpace = false;
                continue;
            }
            inDigit = false;
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) { sig.Append(' '); lastSpace = true; }
                continue;
            }
            sig.Append(c);
            lastSpace = false;
        }
        return sig.ToString().Trim();
    }

    // Decoration sometimes wraps a page number: dashes, dots, bullets, brackets.
    private static readonly char[] PageNumberTrim =
        { ' ', '-', '–', '—', '·', '•', '.', '[', ']', '(', ')', '|' };
    private static readonly Regex PageWordPrefix =
        new(@"^(?:page|pg|p\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Strict roman numeral (i, iv, xii, …) — not just letters from {i,v,x,l,c,d,m},
    // so real words like "did" or "mill" are NOT mistaken for numerals.
    private static readonly Regex Roman =
        new(@"^m{0,4}(cm|cd|d?c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A line that is nothing but a page number: bare digits or a roman numeral,
    // optionally prefixed with "Page"/"p." and wrapped in dashes/brackets/dots.
    private static bool IsPageNumber(PageLine line)
    {
        string core = string.Join(" ", line.Words.Select(w => w.Text))
            .Trim().Trim(PageNumberTrim).Trim();
        core = PageWordPrefix.Replace(core, "").Trim();
        if (core.Length == 0) return false;
        if (core.Length <= 4 && core.All(char.IsDigit)) return true; // arabic page no.
        return Roman.IsMatch(core);                                  // roman page no.
    }
}
