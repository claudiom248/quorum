using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Quorum.SampleKnowledgeBase;

namespace Quorum.SampleContractReferenceData;

/// <summary>
/// The five tools of the sample reference service. They hold what the knowledge bases cannot: an indexed chunk says what a
/// contract costs in euros in its own year, never what that was worth in another currency or is worth now, and never which
/// days between two dates a Milan office would actually have to send a notice. The tools read fixed tables and a calendar;
/// they change nothing, in this process or in the corpora.
/// </summary>
[McpServerToolType]
internal sealed class ReferenceDataTools
{
    /// <summary>The day the contracts of the sample live around; it mirrors the seeder's own date.</summary>
    internal static readonly DateOnly BusinessDate = new(2025, 10, 31);

    private const int MaximumWindowDays = 1100;

    [McpServerTool(Name = "reference_data_summary", ReadOnly = true, OpenWorld = false),
    Description("What this reference service is and the exact codes it answers with: the currencies, the countries, the years of its series, the local holidays of the company's sites. Read it before the other tools when a code or a date format is not certain.")]
    public static string Summary()
    {
        var cities = string.Join(", ", HolidayCalendar.CitiesWithAnExtraDay);
        var currencies = string.Join(" ", ExchangeRates.Currencies);
        var countries = string.Join(" ", PriceIndex.Countries);
        return string.Join(
            '\n',
            "reference_data: the rates, the price indices and the calendars of the sample company. The figures are invented but fixed: the same question always gives the same answer.",
            $"base_currency: {ExchangeRates.Base}",
            $"currencies: {currencies}",
            $"countries: {countries}",
            $"local currency of each: EUR for IT DE ES FR NL, USD for US, GBP for UK, CHF for CH, JPY for JP",
            $"series cover the years {ExchangeRates.FirstYear} to {ExchangeRates.LastYear}; a calendar answers for any year",
            $"cities keeping a day of their own: {cities} (the other site cities keep the national calendar)",
            "a fixed day that falls on a weekend is observed on the Monday after",
            $"the company lives around {Iso(BusinessDate)}: a date in a question is a contract's date, not today's");
    }

    [McpServerTool(Name = "fx_rate", ReadOnly = true, OpenWorld = false),
    Description("The monthly rate between two currencies on a date: what one unit of the first is worth in the second on the series of the reference service. A contract's value in euros lives in the knowledge base; what it was worth in another currency lives only here.")]
    public static string Rate(
        [Description("The date of the quote: ISO (2023-06-15), a month (2023-06) or a bare year (2023); a day is taken to its month, a year to its middle")] string date,
        [Description("The currency the amount is in: EUR, USD, GBP, CHF or JPY")] string from_currency,
        [Description("The currency to state it in: EUR, USD, GBP, CHF or JPY")] string to_currency)
    {
        var when = ParseDate(date, "date");
        var from = ParseCurrency(from_currency, "from_currency");
        var to = ParseCurrency(to_currency, "to_currency");
        var month = new DateOnly(when.Year, when.Month, 1);

        if (from == to)
        {
            return $"fx {MonthLabel(month)}: {from} to {to} is one currency; 1 {from} = 1 {to} by definition.";
        }

        var fromPerEur = ExchangeRates.PerEur(from, month);
        var toPerEur = ExchangeRates.PerEur(to, month);
        if (fromPerEur is null || toPerEur is null)
        {
            throw new McpException(
                $"The series covers {ExchangeRates.FirstYear} to {ExchangeRates.LastYear}; {MonthLabel(month)} has no rates. Say the year of the contract, not today.");
        }

        var direct = (double)toPerEur / (double)fromPerEur;
        var inverse = (double)fromPerEur / (double)toPerEur;
        return string.Join(
            '\n',
            $"fx {MonthLabel(month)}: 1 {from} = {ExchangeRates.Format(from, direct)} {to} · 1 {to} = {ExchangeRates.Format(to, inverse)} {from}",
            $"per 1 EUR that month the series says {ExchangeRates.Format(from, (double)fromPerEur)} {from} and {ExchangeRates.Format(to, (double)toPerEur)} {to} (a monthly average of an invented series)");
    }

    [McpServerTool(Name = "price_index", ReadOnly = true, OpenWorld = false),
    Description("The consumer price index of one country between two years (2018 = 100), optionally applied to an amount: what the same money could buy, or what the same paper is worth in the prices of another year. The year of a contract is in the knowledge base; what its euros are worth years later is only here.")]
    public static string Index(
        [Description("Country of the index, like IT or JP: one of the nine codes of the company")] string country,
        [Description("The year the value belongs to (2015 to 2025)")] int from_year,
        [Description("The year to state it in (2015 to 2025)")] int to_year,
        [Description("Optional: an amount to revalue with the index; leave it out, or send 0, for the index alone")] double amount = 0,
        [Description("The currency of the amount, for the label; defaults to EUR")] string? currency = null)
    {
        var where = ParseCountry(country, "country");
        var then = ParseIndexYear(from_year, "from_year");
        var now = ParseIndexYear(to_year, "to_year");
        var first = ValueAt(where, then);
        var second = ValueAt(where, now);
        var local = PriceIndex.CurrencyOf[where];

        var factor = (double)second / (double)first;
        var percent = (factor - 1) * 100;
        var line = $"price_index {where} {then}→{now}: {PriceIndex.Format((double)first)} → {PriceIndex.Format((double)second)} · ×{factor.ToString("0.000", CultureInfo.InvariantCulture)} ({Signed(percent)}%), the index of {local} prices (invented, fixed)";

        // A plain number with a default keeps the schema free of a null union, which small models fill in badly.
        if (amount == 0)
        {
            return line;
        }

        var what = amount;
        if (what < 0)
        {
            throw new McpException("The amount to revalue must be a positive number.");
        }

        var stamp = currency is null ? "EUR" : ParseCurrency(currency, "currency");
        var revalued = what * factor;
        return string.Join(
            '\n',
            line,
            $"revalued: {Number(what)} {stamp} of {then} at the {where} prices of {now}: {Number(revalued)} {stamp}");

        static double ValueAt(string country, int year) => PriceIndex.Value(country, year)
            ?? throw new McpException(
                $"The index covers {PriceIndex.FirstYear} to {PriceIndex.LastYear}; {country} has no value for {year}.");
    }

    [McpServerTool(Name = "working_days_between", ReadOnly = true, OpenWorld = false),
    Description("How many days of a window are working days in one country (and in one of its site cities), counting weekends and the holidays of that calendar, a fixed day on a weekend observed on the Monday after. Neither a knowledge base nor a lookup can tell this: they hold the dates of the agreements, not the calendar they sit in.")]
    public static string WorkingDays(
        [Description("The first day of the window, ISO (2026-03-02)")] string from,
        [Description("The last day of the window, ISO, included (2026-05-31)")] string to,
        [Description("Country whose calendar counts, like IT or DE")] string country,
        [Description("Optional city of a site (Milan, Madrid, Berlin...): adds the day it keeps; a bare site id also works")] string? city = null)
    {
        var first = ParseDate(from, "from");
        var last = ParseDate(to, "to");
        var where = ParseCountry(country, "country");
        var which = ParseCity(city, "city");

        var span = last.DayNumber - first.DayNumber;
        if (span < 0)
        {
            throw new McpException($"The window is backwards: {Iso(first)} comes after {Iso(last)}.");
        }

        if (span > MaximumWindowDays)
        {
            throw new McpException($"The window is {span} days; this tool counts at most {MaximumWindowDays} at a time. Ask for one quarter, or for the year.");
        }

        var working = HolidayCalendar.CountWorkingDays(first, last, where, which);
        var total = span + 1;
        var holidays = HolidayCalendar.HolidaysIn(first, last, where, which);
        var where_label = which is null ? where : $"{where} ({which})";
        var lines = new List<string>
        {
            $"working_days {where_label} {Iso(first)}..{Iso(last)}: {working} working of {total} days · {total - working} not worked",
        };
        if (holidays.Count > 0)
        {
            var shown = holidays.Take(8).Select(holiday => $"{Iso(holiday.Day)} {holiday.Name}");
            var rest = holidays.Count > 8 ? $" · +{holidays.Count - 8} more" : string.Empty;
            var onWeekend = holidays.Count(holiday => HolidayCalendar.IsWeekend(holiday.Day));
            var weekendNote = onWeekend > 0 ? $" · {onWeekend} of them fall on a weekend" : string.Empty;
            lines.Add($"holidays in the window: {string.Join(", ", shown)}{rest}{weekendNote}");
        }

        return string.Join('\n', lines);
    }

    [McpServerTool(Name = "notice_deadline", ReadOnly = true, OpenWorld = false),
    Description("The date a notice must be sent to end an agreement on its end date, counting the stated days back on the calendar of the country and sliding off a non-working day to the working day before. Use it after a knowledge base or a lookup gave the end date and the notice period: this tool does not search contracts, it dates the deadline.")]
    public static string NoticeDeadline(
        [Description("The date the agreement ends, ISO (2026-03-31): take it from the contract, do not invent it")] string end_date,
        [Description("The notice period in calendar days, as the contract states it (1 to 730)")] int notice_days,
        [Description("Country whose calendar decides the working days, like IT or DE")] string country,
        [Description("Optional city of a site: Milan loses 7 December to Saint Ambrose, and the deadline moves")] string? city = null)
    {
        var end = ParseDate(end_date, "end_date");
        var where = ParseCountry(country, "country");
        var which = ParseCity(city, "city");
        if (notice_days is < 1 or > 730)
        {
            throw new McpException($"notice_days must be between 1 and 730 calendar days; the contract says how many, in its notice article.");
        }

        var nominal = end.AddDays(-notice_days);
        var deadline = HolidayCalendar.PreviousWorkingDay(nominal, where, which);
        var span = deadline.DayNumber - nominal.DayNumber;
        var label = which is null ? where : $"{where} ({which})";

        var lines = new List<string>
        {
            $"notice_deadline {label}: {notice_days} calendar days before the end {Iso(end)}",
            $"  nominal date: {Iso(nominal)} ({Weekday(nominal)})",
        };
        if (span != 0)
        {
            var reason = HolidayCalendar.IsWeekend(nominal) ? "a weekend" : "a holiday";
            lines.Add($"  not a working day ({reason}), so the notice must be in by {Iso(deadline)} ({Weekday(deadline)}), {-span} day{(-span == 1 ? string.Empty : "s")} earlier");
        }
        else
        {
            lines.Add($"  a working day: the deadline stays {Iso(deadline)}");
        }

        var working = HolidayCalendar.CountWorkingDays(deadline, end, where, which);
        lines.Add($"  between the deadline and the end: {(end.DayNumber - deadline.DayNumber) + 1} calendar days, {working} of them worked");
        if (HolidayCalendar.Holidays(end.Year, where, which).Any(holiday => holiday.Day == end) || HolidayCalendar.IsWeekend(end))
        {
            lines.Add($"  note: the agreement ends on a non-working day ({(HolidayCalendar.IsWeekend(end) ? "a weekend" : "a holiday")})");
        }

        return string.Join('\n', lines);
    }

    private static DateOnly ParseDate(string text, string parameter)
    {
        var value = (text ?? string.Empty).Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return new DateOnly(month.Year, month.Month, 1);
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 2000 and <= 2100)
        {
            return new DateOnly(year, 7, 1);
        }

        throw new McpException($"The {parameter} must be an ISO date (2026-03-31), a month (2026-03) or a year (2026): '{text}' is none of them.");
    }

    private static string ParseCurrency(string text, string parameter)
    {
        var value = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (!ExchangeRates.Knows(value))
        {
            throw new McpException($"The {parameter} must be one of {string.Join(", ", ExchangeRates.Currencies)}: '{text}' is not a currency of this service.");
        }

        return value;
    }

    private static string ParseCountry(string text, string parameter)
    {
        var value = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (!PriceIndex.Knows(value))
        {
            throw new McpException($"The {parameter} must be one of {string.Join(", ", PriceIndex.Countries)}: '{text}' has no calendar here.");
        }

        return value;
    }

    private static string? ParseCity(string? text, string parameter)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var wanted = text.Trim();
        var city = HolidayCalendar.NormalisedCity(wanted);
        if (city is null)
        {
            throw new McpException(
                $"'{wanted}' is not a city of the company; the {parameter} only matters for its local day. Leave it out, or use one of: {string.Join(", ", CompanyFacts.Sites.Select(site => site.City).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.");
        }

        return city;
    }

    private static int ParseIndexYear(int year, string parameter)
    {
        if (year is < PriceIndex.FirstYear or > PriceIndex.LastYear)
        {
            throw new McpException($"The {parameter} must be between {PriceIndex.FirstYear} and {PriceIndex.LastYear}, the years of the index; '{year}' is outside.");
        }

        return year;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string MonthLabel(DateOnly month) => $"{month.ToString("MMMM", CultureInfo.InvariantCulture)} {month.Year}";

    private static string Weekday(DateOnly date) => date.ToString("dddd", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Signed(double value) => value.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
}
