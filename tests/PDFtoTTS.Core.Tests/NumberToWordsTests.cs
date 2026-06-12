using PDFtoTTS.Core.TextPipeline;

namespace PDFtoTTS.Core.Tests;

public class NumberToWordsTests
{
    [Theory]
    [InlineData(0, "zero")]
    [InlineData(7, "seven")]
    [InlineData(13, "thirteen")]
    [InlineData(20, "twenty")]
    [InlineData(42, "forty two")]
    [InlineData(100, "one hundred")]
    [InlineData(105, "one hundred five")]
    [InlineData(1250, "one thousand two hundred fifty")]
    [InlineData(1_000_000, "one million")]
    [InlineData(-5, "minus five")]
    public void Cardinal_spells_whole_numbers(long n, string expected) =>
        Assert.Equal(expected, NumberToWords.Cardinal(n));

    [Theory]
    [InlineData(1, "first")]
    [InlineData(2, "second")]
    [InlineData(3, "third")]
    [InlineData(4, "fourth")]
    [InlineData(5, "fifth")]
    [InlineData(8, "eighth")]
    [InlineData(9, "ninth")]
    [InlineData(12, "twelfth")]
    [InlineData(20, "twentieth")]
    [InlineData(21, "twenty first")]
    [InlineData(100, "one hundredth")]
    public void Ordinal_spells_ordinals(long n, string expected) =>
        Assert.Equal(expected, NumberToWords.Ordinal(n));

    [Theory]
    [InlineData(1999, "nineteen ninety nine")]
    [InlineData(1900, "nineteen hundred")]
    [InlineData(1905, "nineteen oh five")]
    [InlineData(2000, "two thousand")]
    [InlineData(2007, "two thousand seven")]
    [InlineData(2024, "twenty twenty four")]
    public void Year_reads_naturally(int year, string expected) =>
        Assert.Equal(expected, NumberToWords.Year(year));

    [Theory]
    [InlineData("3.14", "three point one four")]
    [InlineData("3.5", "three point five")]
    [InlineData("0.07", "zero point zero seven")]
    public void Decimal_spells_fraction_digit_by_digit(string number, string expected) =>
        Assert.Equal(expected, NumberToWords.Decimal(number));
}
