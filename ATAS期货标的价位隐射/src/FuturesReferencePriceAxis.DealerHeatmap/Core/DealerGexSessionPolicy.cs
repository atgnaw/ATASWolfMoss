namespace WolfMoss.ATAS.PriceMapping.Core;

public static class DealerGexSessionPolicy
{
    public static DealerGexState ResolveState(
        DateTime utcNow,
        DealerGexFrame frame)
    {
        if (!NyseTradingCalendar.IsRegularTradingHours(utcNow))
            return DealerGexState.Closed;

        var eastern = UsMarketClock.ToEastern(utcNow);

        if (frame.SessionDateEt != DateOnly.FromDateTime(eastern)
            || !string.Equals(frame.ApiState, "fresh", StringComparison.OrdinalIgnoreCase))
        {
            return DealerGexState.Frozen;
        }

        return DealerGexState.Live;
    }
}
