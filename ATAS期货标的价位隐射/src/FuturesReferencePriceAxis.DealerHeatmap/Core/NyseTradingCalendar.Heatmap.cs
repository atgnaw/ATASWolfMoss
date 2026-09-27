namespace WolfMoss.ATAS.PriceMapping.Core;

// Keep the generic trading calendar usable without Nightwatch models.
public static partial class NyseTradingCalendar
{
    public static DealerHeatmapTarget ResolveTarget(DateTime utcTime, string ticker)
    {
        var eastern = UsMarketClock.ToEastern(utcTime);
        var date = eastern.Date;
        if (IsTradingDay(date) && eastern.TimeOfDay <= GetRegularClose(date) + FinalPublicationGrace)
            return new(ticker.ToUpperInvariant(), DateOnly.FromDateTime(date),
                eastern.TimeOfDay >= RegularOpen ? DealerHeatmapSessionState.RegularTradingHours
                    : DealerHeatmapSessionState.NextSession);
        return new(ticker.ToUpperInvariant(), DateOnly.FromDateTime(GetNextTradingDay(date)),
            DealerHeatmapSessionState.NextSession);
    }
}
