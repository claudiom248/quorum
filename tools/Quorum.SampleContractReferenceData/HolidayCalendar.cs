using Quorum.SampleKnowledgeBase;

namespace Quorum.SampleContractReferenceData;

/// <summary>
/// The calendar the service answers with: the national holidays of the nine countries, the extra day kept by the city of a
/// site, and the rule of an office — a fixed day that falls on a weekend is observed on the Monday after. The dates follow a
/// plausible Europe, not an official gazette: this is a sample, and what matters is that the same window always counts the
/// same working days.
/// </summary>
internal static class HolidayCalendar
{
    internal sealed record Holiday(DateOnly Day, string Name);

    /// <summary>The day a city keeps on top of its country, when the sample has a site there.</summary>
    private static readonly Dictionary<string, (int Month, int Day, string Name)> CityDays = new(StringComparer.Ordinal)
    {
        ["Milan"] = (12, 7, "Saint Ambrose"),
        ["Rome"] = (6, 29, "Saints Peter and Paul, kept only in Rome"),
        ["Turin"] = (6, 24, "Saint John the Baptist"),
        ["Berlin"] = (1, 6, "Epiphany, kept by some Länder and not by Hamburg"),
        ["Madrid"] = (5, 15, "San Isidro"),
        ["Barcelona"] = (9, 24, "La Mercè"),
    };

    /// <summary>The cities of the sample sites that keep a day of their own, for the summary.</summary>
    public static IReadOnlyList<string> CitiesWithAnExtraDay =>
        [.. CompanyFacts.Sites
            .Select(site => site.City)
            .Distinct(StringComparer.Ordinal)
            .Where(city => CityDays.ContainsKey(city))
            .OrderBy(city => city, StringComparer.Ordinal)];

    /// <summary>The canonical spelling of a sample city, so the extra day is found whatever case the model used.</summary>
    public static string? NormalisedCity(string city) =>
        CompanyFacts.Sites.FirstOrDefault(site =>
            site.City.Equals(city, StringComparison.OrdinalIgnoreCase) || site.Id.Equals(city, StringComparison.OrdinalIgnoreCase))?.City;

    public static bool IsWeekend(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Every non-working date of a country (and city) inside a year, observed days merged, in date order.</summary>
    public static IReadOnlyList<Holiday> Holidays(int year, string country, string? city)
    {
        var days = new List<Holiday>(National(year, country));
        if (city is not null && CityDays.TryGetValue(city, out var extra))
        {
            days.Add(new Holiday(new DateOnly(year, extra.Month, extra.Day), extra.Name));
        }

        return
        [
            .. days
                .Select(Observe)
                .GroupBy(holiday => holiday.Day)
                .Select(group => new Holiday(group.Key, string.Join(" and ", group.Select(holiday => holiday.Name))))
                .OrderBy(holiday => holiday.Day),
        ];
    }

    public static bool IsWorkingDay(DateOnly day, string country, string? city) =>
        !IsWeekend(day) && !Holidays(day.Year, country, city).Any(holiday => holiday.Day == day);

    /// <summary>The non-working dates of a window, in order.</summary>
    public static IReadOnlyList<Holiday> HolidaysIn(DateOnly from, DateOnly to, string country, string? city)
    {
        var found = new List<Holiday>();
        for (var year = from.Year; year <= to.Year; year++)
        {
            found.AddRange(Holidays(year, country, city).Where(holiday => holiday.Day >= from && holiday.Day <= to));
        }

        return found;
    }

    /// <summary>Working days in the window, both ends included.</summary>
    public static int CountWorkingDays(DateOnly from, DateOnly to, string country, string? city)
    {
        var count = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (IsWorkingDay(day, country, city))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The working day a date falls on, or the one before it. Terminates: every week has four of them.</summary>
    public static DateOnly PreviousWorkingDay(DateOnly day, string country, string? city)
    {
        var found = day;
        while (!IsWorkingDay(found, country, city))
        {
            found = found.AddDays(-1);
        }

        return found;
    }

    private static Holiday Observe(Holiday holiday) =>
        // A fixed day on a Saturday or a Sunday is taken on the Monday after, the way an office moves it.
        holiday.Day.DayOfWeek switch
        {
            DayOfWeek.Saturday => holiday with { Day = holiday.Day.AddDays(2) },
            DayOfWeek.Sunday => holiday with { Day = holiday.Day.AddDays(1) },
            _ => holiday,
        };

    private static IReadOnlyList<Holiday> National(int year, string country) => country switch
    {
        "IT" =>
        [
            Fixed(year, 1, 1, "New Year"), Fixed(year, 1, 6, "Epiphany"), Movable(Easter(year), 1, "Easter Monday"),
            Fixed(year, 5, 1, "Labour Day"), Fixed(year, 6, 2, "Republic Day"), Fixed(year, 8, 15, "Assumption"),
            Fixed(year, 11, 1, "All Saints"), Fixed(year, 12, 25, "Christmas"), Fixed(year, 12, 26, "Saint Stephen"),
        ],
        "US" =>
        [
            Fixed(year, 1, 1, "New Year"), LastWeekday(May(year), DayOfWeek.Monday, "Memorial Day"),
            Fixed(year, 7, 4, "Independence Day"), NthWeekday(September(year), DayOfWeek.Monday, 1, "Labor Day"),
            Fixed(year, 11, 11, "Veterans Day"), NthWeekday(November(year), DayOfWeek.Thursday, 4, "Thanksgiving"),
            Fixed(year, 12, 25, "Christmas"),
        ],
        "DE" =>
        [
            Fixed(year, 1, 1, "New Year"), Movable(Easter(year), -2, "Good Friday"), Movable(Easter(year), 1, "Easter Monday"),
            Fixed(year, 5, 1, "Labour Day"), Movable(Easter(year), 39, "Ascension"), Movable(Easter(year), 50, "Whit Monday"),
            Fixed(year, 10, 3, "German Unity"), Fixed(year, 12, 25, "Christmas"), Fixed(year, 12, 26, "Saint Stephen"),
        ],
        "UK" =>
        [
            Fixed(year, 1, 1, "New Year"), Movable(Easter(year), -2, "Good Friday"), Movable(Easter(year), 1, "Easter Monday"),
            NthWeekday(May(year), DayOfWeek.Monday, 1, "the early May bank holiday"), LastWeekday(August(year), DayOfWeek.Monday, "the summer bank holiday"),
            Fixed(year, 12, 25, "Christmas"), Fixed(year, 12, 26, "Boxing Day"),
        ],
        "CH" =>
        [
            Fixed(year, 1, 1, "New Year"), Fixed(year, 1, 2, "Berchtold's Day"), Movable(Easter(year), -2, "Good Friday"),
            Movable(Easter(year), 1, "Easter Monday"), Movable(Easter(year), 39, "Ascension"), Movable(Easter(year), 50, "Whit Monday"),
            Fixed(year, 8, 1, "National Day"), Fixed(year, 12, 25, "Christmas"), Fixed(year, 12, 26, "Saint Stephen"),
        ],
        "FR" =>
        [
            Fixed(year, 1, 1, "New Year"), Movable(Easter(year), 1, "Easter Monday"), Fixed(year, 5, 1, "Labour Day"),
            Fixed(year, 5, 8, "Victory in 1945"), Movable(Easter(year), 39, "Ascension"), Movable(Easter(year), 50, "Whit Monday"),
            Fixed(year, 7, 14, "Bastille Day"), Fixed(year, 8, 15, "Assumption"), Fixed(year, 11, 1, "All Saints"),
            Fixed(year, 11, 11, "Armistice in 1918"), Fixed(year, 12, 25, "Christmas"),
        ],
        "ES" =>
        [
            Fixed(year, 1, 1, "New Year"), Fixed(year, 1, 6, "Epiphany"), Movable(Easter(year), -2, "Good Friday"),
            Fixed(year, 5, 1, "Labour Day"), Fixed(year, 8, 15, "Assumption"), Fixed(year, 10, 12, "National Day"),
            Fixed(year, 11, 1, "All Saints"), Fixed(year, 12, 6, "Constitution Day"), Fixed(year, 12, 8, "Immaculate Conception"),
            Fixed(year, 12, 25, "Christmas"),
        ],
        "NL" =>
        [
            Fixed(year, 1, 1, "New Year"), Movable(Easter(year), 1, "Easter Monday"), Fixed(year, 4, 27, "Kings Day"),
            Fixed(year, 5, 5, "Liberation Day"), Movable(Easter(year), 39, "Ascension"), Movable(Easter(year), 50, "Whit Monday"),
            Fixed(year, 12, 25, "Christmas"), Fixed(year, 12, 26, "Second Christmas Day"),
        ],
        "JP" =>
        [
            Fixed(year, 1, 1, "New Year"), NthWeekday(January(year), DayOfWeek.Monday, 2, "Coming of Age"),
            Fixed(year, 2, 11, "Foundation Day"), Fixed(year, 2, 23, "the Emperor's Birthday"), Equinox(year, true),
            Fixed(year, 4, 29, "Showa Day"), Fixed(year, 5, 3, "Constitution Memorial"), Fixed(year, 5, 4, "Greenery Day"),
            Fixed(year, 5, 5, "Children's Day"), NthWeekday(July(year), DayOfWeek.Monday, 3, "Marine Day"),
            Fixed(year, 8, 11, "Mountain Day"), NthWeekday(September(year), DayOfWeek.Monday, 3, "Respect for the Aged"), Equinox(year, false),
            NthWeekday(October(year), DayOfWeek.Monday, 2, "Sports Day"), Fixed(year, 11, 3, "Culture Day"),
            Fixed(year, 11, 23, "Labour Thanksgiving Day"), Fixed(year, 12, 29, "the year-end closing"), Fixed(year, 12, 30, "the year-end closing"), Fixed(year, 12, 31, "the year-end closing"),
        ],
        _ => [],
    };

    private static (int Year, int Month) January(int year) => (year, 1);

    private static (int Year, int Month) May(int year) => (year, 5);

    private static (int Year, int Month) July(int year) => (year, 7);

    private static (int Year, int Month) August(int year) => (year, 8);

    private static (int Year, int Month) September(int year) => (year, 9);

    private static (int Year, int Month) October(int year) => (year, 10);

    private static (int Year, int Month) November(int year) => (year, 11);

    private static Holiday Fixed(int year, int month, int day, string name) => new(new DateOnly(year, month, day), name);

    private static Holiday Movable(DateOnly anchor, int days, string name) => new(anchor.AddDays(days), name);

    /// <summary>The Easter Sunday of a year: the anonymous Gregorian computus, deterministic without a table of dates.</summary>
    private static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>The nth <paramref name="weekday"/> of a month, counting from the first.</summary>
    private static Holiday NthWeekday((int Year, int Month) month, DayOfWeek weekday, int n, string name)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = (int)weekday - (int)first.DayOfWeek;
        if (offset < 0)
        {
            offset += 7;
        }

        return new(first.AddDays(offset + ((n - 1) * 7)), name);
    }

    /// <summary>The last <paramref name="weekday"/> of a month.</summary>
    private static Holiday LastWeekday((int Year, int Month) month, DayOfWeek weekday, string name)
    {
        var end = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        var offset = (int)end.DayOfWeek - (int)weekday;
        if (offset < 0)
        {
            offset += 7;
        }

        return new(end.AddDays(-offset), name);
    }

    /// <summary>The Japanese equinox holidays move between a few days: the sample fixes one date each.</summary>
    private static Holiday Equinox(int year, bool spring) =>
        new(new DateOnly(year, spring ? 3 : 9, spring ? 20 : 23), spring ? "Vernal Equinox" : "Autumnal Equinox");
}
