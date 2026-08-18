namespace WolfMoss.ATAS.PriceMapping.Core;

public static class NyseTradingCalendar
{
    public static readonly TimeSpan RegularOpen = new(9, 30, 0);
    public static readonly TimeSpan RegularClose = new(16, 0, 0);
    public static readonly TimeSpan EarlyClose = new(13, 0, 0);
    public static readonly TimeSpan FinalPublicationGrace = TimeSpan.FromMinutes(1);

    public static bool IsTradingDay(DateTime date)
    {
        date = date.Date;
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
               && !UsMarketClock.IsFullEquityMarketHoliday(date);
    }

    public static bool IsEarlyClose(DateTime date)
    {
        date = date.Date;

        if (!IsTradingDay(date))
            return false;

        var thanksgiving = NthWeekday(date.Year, 11, DayOfWeek.Thursday, 4);
        return date == thanksgiving.AddDays(1)
               || date.Month == 7 && date.Day == 3
               || date.Month == 12 && date.Day == 24;
    }

    public static TimeSpan GetRegularClose(DateTime date)
        => IsEarlyClose(date) ? EarlyClose : RegularClose;

    public static bool IsRegularTradingHours(DateTime utcTime)
    {
        var eastern = UsMarketClock.ToEastern(utcTime);
        return IsTradingDay(eastern.Date)
               && eastern.TimeOfDay >= RegularOpen
               && eastern.TimeOfDay <= GetRegularClose(eastern.Date);
    }

    public static DealerHeatmapTarget ResolveTarget(DateTime utcTime, string ticker)
    {
        var eastern = UsMarketClock.ToEastern(utcTime);
        var date = eastern.Date;

        if (IsTradingDay(date))
        {
            var close = GetRegularClose(date);

            if (eastern.TimeOfDay <= close + FinalPublicationGrace)
            {
                var state = eastern.TimeOfDay >= RegularOpen
                    ? DealerHeatmapSessionState.RegularTradingHours
                    : DealerHeatmapSessionState.NextSession;
                return new DealerHeatmapTarget(
                    ticker.ToUpperInvariant(),
                    DateOnly.FromDateTime(date),
                    state);
            }
        }

        return new DealerHeatmapTarget(
            ticker.ToUpperInvariant(),
            DateOnly.FromDateTime(GetNextTradingDay(date)),
            DealerHeatmapSessionState.NextSession);
    }

    public static DateTime GetNextTradingDay(DateTime date)
    {
        var candidate = date.Date.AddDays(1);

        while (!IsTradingDay(candidate))
            candidate = candidate.AddDays(1);

        return candidate;
    }

    public static DateTime GetNextSessionTransitionUtc(DateTime utcTime)
    {
        var eastern = UsMarketClock.ToEastern(utcTime);
        var date = eastern.Date;

        if (IsTradingDay(date))
        {
            var close = GetRegularClose(date);

            if (eastern.TimeOfDay < RegularOpen)
                return EasternToUtc(date + RegularOpen);

            if (eastern.TimeOfDay <= close + FinalPublicationGrace)
                return EasternToUtc(date + close + FinalPublicationGrace);
        }

        var next = GetNextTradingDay(date);
        return EasternToUtc(next + RegularOpen);
    }

    public static DateTime EasternToUtc(DateTime easternWallClock)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(easternWallClock, DateTimeKind.Unspecified),
            UsMarketClock.EasternTimeZone);

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
}
