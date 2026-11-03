using System.Globalization;

namespace Quorum.SampleContractReferenceData;

/// <summary>
/// The consumer price indices of the nine countries of the company, one value per year 2015 to 2025 with 2018 = 100. The
/// figures are invented, the shape is plausible: a flat 2015 to 2019 and the bump of 2022 and 2023. The service answers
/// "what was that money worth then, what is it worth now", which no indexed chunk of the contracts can say.
/// </summary>
internal static class PriceIndex
{
    public const int FirstYear = 2015;

    public const int LastYear = 2025;

    /// <summary>The local currency of each country, the same nine the vendors' nationalities and the sites use.</summary>
    public static readonly IReadOnlyDictionary<string, string> CurrencyOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CH"] = "CHF",
        ["DE"] = "EUR",
        ["ES"] = "EUR",
        ["FR"] = "EUR",
        ["IT"] = "EUR",
        ["JP"] = "JPY",
        ["NL"] = "EUR",
        ["UK"] = "GBP",
        ["US"] = "USD",
    };

    public static IEnumerable<string> Countries => CurrencyOf.Keys.OrderBy(country => country, StringComparer.Ordinal);

    /// <summary>The index per year, 2015 first, 2018 = 100.</summary>
    private static readonly Dictionary<string, double[]> Indexes = new(StringComparer.Ordinal)
    {
        ["CH"] = [98.4, 97.9, 98.6, 100.0, 100.6, 101.4, 103.9, 106.8, 108.7, 109.6, 110.8],
        ["DE"] = [97.7, 98.3, 99.4, 100.0, 101.4, 103.2, 108.5, 115.4, 118.3, 119.9, 122.6],
        ["ES"] = [96.8, 96.4, 98.0, 100.0, 100.9, 103.1, 107.6, 114.3, 117.1, 118.4, 120.2],
        ["FR"] = [98.2, 98.5, 99.3, 100.0, 101.2, 103.5, 107.2, 111.0, 113.2, 115.1, 117.0],
        ["IT"] = [95.9, 95.5, 97.3, 100.0, 101.6, 103.3, 108.2, 113.5, 115.9, 118.7, 121.4],
        ["JP"] = [97.5, 96.8, 97.9, 100.0, 101.3, 101.9, 103.1, 106.0, 107.9, 110.6, 113.2],
        ["NL"] = [98.9, 99.1, 99.5, 100.0, 101.1, 103.8, 110.0, 116.9, 118.8, 120.1, 122.8],
        ["UK"] = [97.1, 98.0, 99.2, 100.0, 101.0, 103.2, 107.8, 114.8, 119.3, 121.2, 123.5],
        ["US"] = [96.5, 96.0, 97.9, 100.0, 102.3, 108.0, 115.1, 121.3, 124.0, 125.6, 128.3],
    };

    public static bool Knows(string country) => Indexes.ContainsKey(country);

    /// <summary>The index of a year, or <see langword="null"/> when the country or the year is out of the table.</summary>
    public static double? Value(string country, int year)
    {
        if (!Indexes.TryGetValue(country, out var series) || year < FirstYear || year > LastYear)
        {
            return null;
        }

        return series[year - FirstYear];
    }

    public static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
