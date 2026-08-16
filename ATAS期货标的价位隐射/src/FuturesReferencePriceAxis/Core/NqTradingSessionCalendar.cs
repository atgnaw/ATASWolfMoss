namespace WolfMoss.ATAS.PriceMapping.Core;

public static class NqTradingSessionCalendar
{
    private static readonly TimeSpan DailyClose = TimeSpan.FromHours(17);
    private static readonly TimeSpan DailyOpen = TimeSpan.FromHours(18);
    private static readonly TimeSpan EquityHaltStart = new(16, 15, 0);
    private static readonly TimeSpan EquityHaltEnd = new(16, 30, 0);
    public static bool IsOpen(DateTime utcTime)
    {
        var eastern = UsMarketClock.ToEastern(utcTime);
        var time = eastern.TimeOfDay;

        if (eastern.DayOfWeek == DayOfWeek.Saturday)
            return false;

        if (eastern.DayOfWeek == DayOfWeek.Sunday)
            return time >= DailyOpen;

        if (eastern.DayOfWeek == DayOfWeek.Friday && time >= DailyClose)
            return false;

        if (time >= DailyClose && time < DailyOpen)
            return false;

        return time < EquityHaltStart || time >= EquityHaltEnd;
    }
}
