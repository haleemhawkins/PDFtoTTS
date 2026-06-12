using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// Converts extracted source words into a spoken token stream for TTS, applying
/// deterministic rules for numbers, ordinals, currency, percent, units,
/// abbreviations, acronyms, URLs and emails. Every emitted token is tagged with
/// the source word index it derives from, so timestamps can later be projected
/// back onto the original word for highlighting (see design §2.3/§2.4).
/// </summary>
public sealed partial class TextNormalizer
{
    // Multiword phrases that should be spoken (and highlighted) as one unit.
    // Each collapses N source words into a single token spanning their indices.
    private static readonly string[] DefaultPhrases =
    {
        "New York", "New Jersey", "Los Angeles", "San Francisco", "Las Vegas",
        "United States", "United Kingdom", "Hong Kong", "Sri Lanka", "et al"
    };

    private readonly Dictionary<string, string> _phrases;
    private readonly int _maxPhraseWords;

    /// <param name="phrases">
    /// Multiword phrases to collapse (default: a small built-in set). Pass an
    /// empty sequence to disable phrase collapsing.
    /// </param>
    public TextNormalizer(IEnumerable<string>? phrases = null)
    {
        var list = (phrases ?? DefaultPhrases).ToList();
        _phrases = list.ToDictionary(PhraseKey, p => p, StringComparer.Ordinal);
        _maxPhraseWords = list.Count == 0 ? 0 : list.Max(p => p.Split(' ').Length);
    }

    private static string PhraseKey(string phrase) =>
        string.Join(' ', phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dr."] = "Doctor",
        ["mr."] = "Mister",
        ["mrs."] = "Missus",
        ["ms."] = "Miss",
        ["prof."] = "Professor",
        ["st."] = "Saint",
        ["e.g."] = "for example",
        ["i.e."] = "that is",
        ["etc."] = "et cetera",
        ["vs."] = "versus",
        ["approx."] = "approximately"
    };

    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = "kilograms",
        ["g"] = "grams",
        ["mg"] = "milligrams",
        ["km"] = "kilometers",
        ["m"] = "meters",
        ["cm"] = "centimeters",
        ["mm"] = "millimeters",
        ["lb"] = "pounds",
        ["oz"] = "ounces",
        ["ml"] = "milliliters",
        ["l"] = "liters"
    };

    /// <summary>Normalize an ordered list of source words into spoken tokens.</summary>
    public IReadOnlyList<NormToken> Normalize(IReadOnlyList<SourceWord> words)
    {
        var tokens = new List<NormToken>();
        int i = 0;
        while (i < words.Count)
        {
            // N→1: a known multiword phrase becomes one token spanning all the
            // source words it covers (design §2.4).
            if (TryMatchPhrase(words, i, out string phraseText, out int span, out bool phraseEnds))
            {
                tokens.Add(new NormToken(
                    Index: tokens.Count,
                    Text: phraseText,
                    SourceStart: words[i].Index,
                    SourceEnd: words[i + span - 1].Index,
                    EndsSentence: phraseEnds));
                i += span;
                continue;
            }

            var word = words[i];
            var (spoken, endsSentence) = ClassifyWord(word.Text);
            if (spoken.Count == 0)
            {
                // Pure punctuation: drop it, but a terminator promotes the
                // previous token to a sentence boundary.
                if (endsSentence && tokens.Count > 0)
                    tokens[^1] = tokens[^1] with { EndsSentence = true };
                i++;
                continue;
            }

            // 1→N: one source word may expand to several tokens, all tagged with
            // that source word's index.
            for (int k = 0; k < spoken.Count; k++)
            {
                bool last = k == spoken.Count - 1;
                tokens.Add(new NormToken(
                    Index: tokens.Count,
                    Text: spoken[k],
                    SourceStart: word.Index,
                    SourceEnd: word.Index,
                    EndsSentence: last && endsSentence));
            }

            i++;
        }

        return tokens;
    }

    // Greedy longest-match phrase lookup starting at source word `start`.
    private bool TryMatchPhrase(IReadOnlyList<SourceWord> words, int start,
        out string text, out int span, out bool endsSentence)
    {
        int maxLen = Math.Min(_maxPhraseWords, words.Count - start);
        for (int len = maxLen; len >= 2; len--)
        {
            var parts = new string[len];
            for (int k = 0; k < len; k++) parts[k] = StripForKey(words[start + k].Text);
            string candidate = string.Join(' ', parts).ToLowerInvariant();
            if (_phrases.TryGetValue(candidate, out var canonical))
            {
                text = canonical;
                span = len;
                endsSentence = HasSentenceTerminator(words[start + len - 1].Text);
                return true;
            }
        }

        text = string.Empty;
        span = 0;
        endsSentence = false;
        return false;
    }

    private static string StripForKey(string s) =>
        s.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '“', '”', '‘', '’');

    private static bool HasSentenceTerminator(string s)
    {
        string t = s.TrimEnd(')', ']', '"', '\'', '”', '’');
        return t.Length > 0 && t[^1] is '.' or '?' or '!';
    }

    /// <summary>
    /// Classify one raw source word into spoken tokens plus whether it ends a
    /// sentence. Exposed for unit testing of individual rules.
    /// </summary>
    public (IReadOnlyList<string> Tokens, bool EndsSentence) ClassifyWord(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return (Array.Empty<string>(), false);

        // Abbreviations are checked first because their trailing period is part
        // of the token, not a sentence terminator.
        if (Abbreviations.TryGetValue(raw, out var expansion))
            return (expansion.Split(' '), false);

        // Peel trailing sentence punctuation and surrounding wrappers, tracking
        // whether a sentence terminator was present.
        bool endsSentence = false;
        string core = raw;
        while (core.Length > 0 && (core[^1] is '.' or '?' or '!' or ')' or ']' or '"' or '\'' or '”' or '’'))
        {
            if (core[^1] is '.' or '?' or '!') endsSentence = true;
            core = core[..^1];
        }

        core = core.TrimStart('(', '[', '"', '\'', '“', '‘', '¿', '¡');

        if (core.Length == 0) return (Array.Empty<string>(), endsSentence);

        try
        {
            return (ClassifyCore(core), endsSentence);
        }
        catch (Exception)
        {
            // A malformed or oversized numeric token (overflow, unexpected format)
            // must never fail the whole document. Fall back to speaking digits
            // individually, or the literal token if it has none.
            return (SpellDigitsOrLiteral(core), endsSentence);
        }
    }

    private static IReadOnlyList<string> SpellDigitsOrLiteral(string core)
    {
        var digits = new List<string>();
        foreach (char c in core)
            if (char.IsDigit(c))
                digits.Add(NumberToWords.Cardinal(c - '0'));
        return digits.Count > 0 ? digits : new[] { core };
    }

    private static IReadOnlyList<string> ClassifyCore(string core)
    {
        // URL / email
        if (core.Contains("://") || EmailRegex().IsMatch(core) || DomainRegex().IsMatch(core))
            return ExpandUrl(core);

        // Currency: leading $ £ or €
        if (core.Length > 1 && core[0] is '$' or '£' or '€' && IsNumeric(core[1..]))
            return ExpandCurrency(core[0], core[1..]);

        // Percent
        if (core.EndsWith('%') && IsNumeric(core[..^1]))
            return Words(ExpandNumber(core[..^1])).Append("percent").ToList();

        // Number + unit, e.g. "5kg", "10km"
        var unitMatch = UnitRegex().Match(core);
        if (unitMatch.Success && Units.TryGetValue(unitMatch.Groups[2].Value, out var unitWord))
            return Words(ExpandNumber(unitMatch.Groups[1].Value)).Concat(Words(unitWord)).ToList();

        // Ordinal, e.g. "3rd", "21st"
        var ordMatch = OrdinalRegex().Match(core);
        if (ordMatch.Success)
            return Words(NumberToWords.Ordinal(long.Parse(
                ordMatch.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture)));

        // Plain number (cardinal / decimal / year)
        if (IsNumeric(core))
            return Words(ExpandNumber(core));

        // Acronym: 2–6 uppercase letters → one spoken token
        if (AcronymRegex().IsMatch(core))
            return new[] { core };

        // Default: keep the word as-is.
        return new[] { core };
    }

    // --- helpers --------------------------------------------------------------

    private static List<string> Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool IsNumeric(string s) => NumericRegex().IsMatch(s);

    private static string ExpandNumber(string s)
    {
        bool hadThousandsSeparator = s.Contains(',');
        s = s.Replace(",", "");
        if (s.Contains('.')) return NumberToWords.Decimal(s);

        // 4-digit values in a typical year range read as years — but a
        // thousands separator ("1,250") means it is a quantity, not a year.
        if (!hadThousandsSeparator && s.Length == 4 &&
            int.TryParse(s, out int y) && y is >= 1000 and <= 2999)
            return NumberToWords.Year(y);

        return NumberToWords.Cardinal(long.Parse(s, CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<string> ExpandCurrency(char symbol, string amount)
    {
        string unit = symbol switch { '£' => "pounds", '€' => "euros", _ => "dollars" };
        string sub = symbol switch { '£' => "pence", '€' => "cents", _ => "cents" };
        amount = amount.Replace(",", "");

        long whole;
        int frac = 0;
        int dot = amount.IndexOf('.');
        if (dot >= 0)
        {
            whole = long.Parse(amount[..dot], CultureInfo.InvariantCulture);
            string fracStr = (amount[(dot + 1)..] + "00")[..2];
            frac = int.Parse(fracStr, CultureInfo.InvariantCulture);
        }
        else
        {
            whole = long.Parse(amount, CultureInfo.InvariantCulture);
        }

        var result = new List<string>();
        result.AddRange(Words(NumberToWords.Cardinal(whole)));
        result.Add(whole == 1 ? Singular(unit) : unit);
        if (frac > 0)
        {
            result.Add("and");
            result.AddRange(Words(NumberToWords.Cardinal(frac)));
            result.Add(frac == 1 ? Singular(sub) : sub);
        }

        return result;
    }

    private static string Singular(string plural) => plural switch
    {
        "dollars" => "dollar",
        "pounds" => "pound",
        "euros" => "euro",
        "cents" => "cent",
        "pence" => "penny",
        _ => plural
    };

    private static IReadOnlyList<string> ExpandUrl(string url)
    {
        // Drop scheme and a leading www.
        url = SchemeRegex().Replace(url, string.Empty);
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = url[4..];

        var sb = new StringBuilder();
        foreach (char c in url)
        {
            sb.Append(c switch
            {
                '.' => " dot ",
                '/' => " slash ",
                '-' => " dash ",
                '_' => " underscore ",
                '@' => " at ",
                ':' => " colon ",
                '?' => " question mark ",
                '=' => " equals ",
                '&' => " and ",
                '#' => " hash ",
                _ => c.ToString()
            });
        }

        return Words(sb.ToString());
    }

    [GeneratedRegex(@"^-?\d[\d,]*(\.\d+)?$")]
    private static partial Regex NumericRegex();

    [GeneratedRegex(@"^([\d,]+(?:\.\d+)?)(st|nd|rd|th)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"^([\d,]+(?:\.\d+)?)([a-zA-Z]+)$")]
    private static partial Regex UnitRegex();

    [GeneratedRegex(@"^[A-Z]{2,6}$")]
    private static partial Regex AcronymRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9-]+\.)+(com|org|net|edu|gov|io|co)(/.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^[a-zA-Z]+://")]
    private static partial Regex SchemeRegex();
}
