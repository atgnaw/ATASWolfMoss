namespace WolfMoss.ATAS.PriceMapping.Core;

public static class UsMarketClock
{
    public static TimeZoneInfo EasternTimeZone { get; } = FindTimeZone(
        "Eastern Standard Time",
        "America/New_York");

    public static TimeZoneInfo CentralTimeZone { get; } = FindTimeZone(
        "Central Standard Time",
        "America/Chicago");

    public static DateTime ToEastern(DateTime utcTime)
        => TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(utcTime), EasternTimeZone);

    public static DateTime NasdaqWallClockToUtc(long encodedMilliseconds)
    {
        var encodedUtc = DateTimeOffset
            .FromUnixTimeMilliseconds(encodedMilliseconds)
            .UtcDateTime;
        var easternWallClock = DateTime.SpecifyKind(
            encodedUtc,
            DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(easternWallClock, EasternTimeZone);
    }

    public static bool IsFullEquityMarketHoliday(DateTime date)
    {
        date = date.Date;
        var year = date.Year;
        return date == ObservedNewYear(new DateTime(year, 1, 1))
               || date == NthWeekday(year, 1, DayOfWeek.Monday, 3)
               || date == NthWeekday(year, 2, DayOfWeek.Monday, 3)
               || date == EasterSunday(year).AddDays(-2)
               || date == LastWeekday(year, 5, DayOfWeek.Monday)
               || year >= 2022 && date == ObservedHoliday(new DateTime(year, 6, 19))
               || date == ObservedHoliday(new DateTime(year, 7, 4))
               || date == NthWeekday(year, 9, DayOfWeek.Monday, 1)
               || date == NthWeekday(year, 11, DayOfWeek.Thursday, 4)
               || date == ObservedHoliday(new DateTime(year, 12, 25));
    }

    public static TimeSpan GetUtcOffset(TimeZoneInfo timeZone, DateTime utcTime)
        => timeZone.GetUtcOffset(NormalizeUtc(utcTime));

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime ObservedHoliday(DateTime holiday)
        => holiday.DayOfWeek switch
        {
            DayOfWeek.Saturday => holiday.AddDays(-1),
            DayOfWeek.Sunday => holiday.AddDays(1),
            _ => holiday
        };

    private static DateTime ObservedNewYear(DateTime holiday)
        => holiday.DayOfWeek == DayOfWeek.Sunday
            ? holiday.AddDays(1)
            : holiday;

    private static DateTime NthWeekday(
        int year,
        int month,
        DayOfWeek dayOfWeek,
        int occurrence)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)dayOfWeek - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (occurrence - 1) * 7);
    }

    private static DateTime LastWeekday(
        int year,
        int month,
        DayOfWeek dayOfWeek)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)dayOfWeek + 7) % 7;
        return last.AddDays(-offset);
    }

    private static DateTime EasterSunday(int year)
    {
        var goldenNumber = year % 19;
        var century = year / 100;
        var yearOfCentury = year % 100;
        var leapCentury = century / 4;
        var centuryRemainder = century % 4;
        var correction = (century + 8) / 25;
        var adjustedCorrection = (century - correction + 1) / 3;
        var epact = (19 * goldenNumber + century - leapCentury
                     - adjustedCorrection + 15) % 30;
        var leapYearOfCentury = yearOfCentury / 4;
        var yearRemainder = yearOfCentury % 4;
        var weekdayCorrection = (32 + 2 * centuryRemainder
                                  + 2 * leapYearOfCentury - epact
                                  - yearRemainder) % 7;
        var finalCorrection = (goldenNumber + 11 * epact
                               + 22 * weekdayCorrection) / 451;
        var month = (epact + weekdayCorrection - 7 * finalCorrection + 114) / 31;
        var day = (epact + weekdayCorrection - 7 * finalCorrection + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }

    private static TimeZoneInfo FindTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new TimeZoneNotFoundException(
            $"Cannot resolve time zone: {string.Join(", ", ids)}");
    }
}
