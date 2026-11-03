using System.Globalization;

namespace Quorum.SampleContractReferenceData;

/// <summary>
/// The rates of the reference service: an invented series per currency per year against the euro, 2015 to 2025, with the
/// months taken by straight interpolation between the yearly values plus a deterministic wobble, so the same month always
/// gives the same number. The 2026 value is only the far end of the 2025 slope. Nothing here is a real rate: a sample
/// should not pretend to know the market.
/// </summary>
internal static class ExchangeRates
{
    public const string Base = "EUR";

    /// <summary>The years the series can answer for.</summary>
    public const int FirstYear = 2015;

    public const int LastYear = 2025;

    public static readonly IReadOnlyList<string> Currencies = ["EUR", "USD", "GBP", "CHF", "JPY"];

    /// <summary>Units of the currency per 1 EUR, one value per year from <see cref="FirstYear"/> (the last value is the slope's end, not a year).</summary>
    private static readonly Dictionary<string, double[]> Yearly = new(StringComparer.Ordinal)
    {
        ["USD"] = [1.090, 1.078, 1.120, 1.180, 1.121, 1.142, 1.182, 1.053, 1.081, 1.084, 1.090, 1.102],
        ["GBP"] = [0.732, 0.792, 0.771, 0.884, 0.873, 0.889, 0.859, 0.852, 0.863, 0.858, 0.853, 0.857],
        ["CHF"] = [1.061, 1.082, 1.091, 1.161, 1.132, 1.076, 1.061, 1.006, 0.967, 0.942, 0.927, 0.915],
        ["JPY"] = [123.1, 127.0, 127.0, 130.2, 121.9, 121.9, 129.8, 138.7, 141.0, 151.9, 157.2, 160.3],
    };

    public static bool Knows(string currency) => currency == Base || Yearly.ContainsKey(currency);

    /// <summary>
    /// Units of <paramref name="currency"/> for 1 EUR in the month <paramref name="date"/> falls in, or <see langword="null"/>
    /// when the currency is unknown or the month is outside the series.
    /// </summary>
    public static double? PerEur(string currency, DateOnly date)
    {
        if (!Knows(currency) || date.Year < FirstYear || date.Year > LastYear)
        {
            return null;
        }

        if (currency == Base)
        {
            return 1;
        }

        var series = Yearly[currency];
        var index = date.Year - FirstYear;
        var from = series[index];
        var to = series[index + 1];
        var value = from + ((to - from) * (date.Month - 1) / 12);
        return value * (1 + Wobble(currency, date));
    }

    /// <summary>The rate as the model reads it: two decimals for the currencies that trade in hundreds, four for the others.</summary>
    public static string Format(string currency, double value) =>
        value.ToString(currency is "JPY" ? "0.00" : "0.0000", CultureInfo.InvariantCulture);

    private static double Wobble(string currency, DateOnly date)
    {
        // A fixed hash and not a random one: every process that asks for June 2023 must get the same June 2023.
        var hash = Hash($"rate:{currency}:{date.Year}:{date.Month}");
        return (((int)(hash % 81)) - 40) / 10_000.0;
    }

    private static uint Hash(string text)
    {
        unchecked
        {
            var value = 2166136261u;
            foreach (var character in text)
            {
                value = (value ^ (uint)character) * 16777619u;
            }

            return value;
        }
    }
}
