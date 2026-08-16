namespace WolfMoss.ATAS.PriceMapping.Core;

public static class UsEquitySessionCalendar
{
    private static readonly TimeSpan ExtendedSessionOpen = TimeSpan.FromHours(4);
    private static readonly TimeSpan ExtendedSessionClose = TimeSpan.FromHours(20);
    public static bool IsQqqExtendedSessionClosed(DateTime nowUtc)
    {
        var eastern = UsMarketClock.ToEastern(nowUtc);
        var date = eastern.Date;

        if (eastern.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return true;

        if (UsMarketClock.IsFullEquityMarketHoliday(date))
            return true;

        return eastern.TimeOfDay < ExtendedSessionOpen
               || eastern.TimeOfDay >= ExtendedSessionClose;
    }

}
