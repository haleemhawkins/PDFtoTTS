using System.Globalization;
using System.Text;

namespace PDFtoTTS.Core.TextPipeline;

/// <summary>
/// Deterministic English number-to-words conversion used by normalization.
/// Pure and side-effect free so it is trivially unit-testable.
/// </summary>
public static class NumberToWords
{
    private static readonly string[] Ones =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight",
        "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen",
        "sixteen", "seventeen", "eighteen", "nineteen"
    };

    private static readonly string[] Tens =
    {
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy",
        "eighty", "ninety"
    };

    private static readonly (long Value, string Name)[] Scales =
    {
        (1_000_000_000_000L, "trillion"),
        (1_000_000_000L, "billion"),
        (1_000_000L, "million"),
        (1_000L, "thousand")
    };

    /// <summary>Spell a whole number, e.g. 1250 → "one thousand two hundred fifty".</summary>
    public static string Cardinal(long n)
    {
        if (n == 0) return "zero";
        if (n < 0) return "minus " + Cardinal(-n);

        var parts = new List<string>();
        foreach (var (value, name) in Scales)
        {
            if (n < value) continue;
            parts.Add(ThreeDigits(n / value));
            parts.Add(name);
            n %= value;
        }

        if (n > 0) parts.Add(ThreeDigits(n));
        return string.Join(" ", parts);
    }

    // 1..999
    private static string ThreeDigits(long n)
    {
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add(Ones[n / 100]);
            parts.Add("hundred");
            n %= 100;
        }

        if (n >= 20)
        {
            parts.Add(Tens[n / 10]);
            if (n % 10 > 0) parts.Add(Ones[n % 10]);
        }
        else if (n > 0)
        {
            parts.Add(Ones[n]);
        }

        return string.Join(" ", parts);
    }

    /// <summary>Spell an ordinal, e.g. 21 → "twenty first", 3 → "third".</summary>
    public static string Ordinal(long n)
    {
        var words = Cardinal(n).Split(' ');
        words[^1] = OrdinalizeWord(words[^1]);
        return string.Join(" ", words);
    }

    private static readonly Dictionary<string, string> OrdinalSpecial = new()
    {
        ["one"] = "first",
        ["two"] = "second",
        ["three"] = "third",
        ["five"] = "fifth",
        ["eight"] = "eighth",
        ["nine"] = "ninth",
        ["twelve"] = "twelfth"
    };

    private static string OrdinalizeWord(string word)
    {
        if (OrdinalSpecial.TryGetValue(word, out var special)) return special;
        if (word.EndsWith('y')) return word[..^1] + "ieth"; // twenty → twentieth
        return word + "th"; // four → fourth, hundred → hundredth
    }

    /// <summary>
    /// Spell a 4-digit year naturally: 1999 → "nineteen ninety nine",
    /// 1905 → "nineteen oh five", 1900 → "nineteen hundred",
    /// 2000 → "two thousand", 2007 → "two thousand seven", 2024 → "twenty twenty four".
    /// Falls back to <see cref="Cardinal"/> outside 1000–9999.
    /// </summary>
    public static string Year(int year)
    {
        if (year is < 1000 or > 9999) return Cardinal(year);

        int hi = year / 100;
        int lo = year % 100;

        if (year is >= 2000 and <= 2009)
            return lo > 0 ? "two thousand " + Cardinal(lo) : "two thousand";
        if (lo == 0) return Cardinal(hi) + " hundred"; // 1900 → nineteen hundred
        if (lo < 10) return Cardinal(hi) + " oh " + Cardinal(lo); // 1905 → nineteen oh five
        return Cardinal(hi) + " " + Cardinal(lo); // 1999 → nineteen ninety nine
    }

    /// <summary>
    /// Spell a decimal: integer part as a cardinal, fractional digits spoken
    /// individually. 3.14 → "three point one four".
    /// </summary>
    public static string Decimal(string number)
    {
        int dot = number.IndexOf('.');
        string intPart = number[..dot];
        string fracPart = number[(dot + 1)..];

        long intValue = long.Parse(intPart.Length == 0 ? "0" : intPart, CultureInfo.InvariantCulture);
        var sb = new StringBuilder(Cardinal(intValue));
        sb.Append(" point");
        foreach (char c in fracPart)
        {
            if (!char.IsDigit(c)) continue;
            sb.Append(' ').Append(Ones[c - '0']);
        }

        return sb.ToString();
    }
}
