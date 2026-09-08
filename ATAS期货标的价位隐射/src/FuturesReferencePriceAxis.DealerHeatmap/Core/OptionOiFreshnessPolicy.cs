namespace WolfMoss.ATAS.PriceMapping.Core;

// This is a local reconfirmation policy, NOT the vendor's clearing/publication date.
public static class OptionOiFreshnessPolicy
{
    public static DateTime ConfirmationStartUtc(DateTime nowUtc)
    {
        if (nowUtc.Year < 1970) return DateTime.MinValue;
        var eastern = UsMarketClock.ToEastern(nowUtc);
        var day = eastern.Date;
        if (eastern.TimeOfDay < TimeSpan.FromHours(8.5)) day = day.AddDays(-1);
        while (!NyseTradingCalendar.IsTradingDay(day)) day = day.AddDays(-1);
        return NyseTradingCalendar.EasternToUtc(day.AddHours(8.5));
    }

    public static bool IsConfirmed(DateTime receivedUtc, DateTime nowUtc)
        => nowUtc.Year >= 1970 && receivedUtc > DateTime.MinValue
           && receivedUtc.Kind == DateTimeKind.Utc && receivedUtc >= ConfirmationStartUtc(nowUtc)
           && receivedUtc <= nowUtc;

    public static DateTime NextRetryUtc(DateTime nowUtc)
    {
        if (nowUtc.Year < 1970) return DateTime.MaxValue;
        var eastern = UsMarketClock.ToEastern(nowUtc);
        var day = eastern.Date;
        if (NyseTradingCalendar.IsTradingDay(day))
            foreach (var minute in new[] { 510, 555, 575 })
            {
                var candidate = NyseTradingCalendar.EasternToUtc(day.AddMinutes(minute));
                if (candidate > nowUtc) return candidate;
            }
        return NyseTradingCalendar.EasternToUtc(
            NyseTradingCalendar.GetNextTradingDay(day).AddHours(8.5));
    }
}
