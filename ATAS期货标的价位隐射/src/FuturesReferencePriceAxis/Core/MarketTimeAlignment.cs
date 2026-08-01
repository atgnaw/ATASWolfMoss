namespace WolfMoss.ATAS.PriceMapping.Core;

public static class MarketTimeAlignment
{
    public static DateTime UtcMinuteToMarketTime(
        DateTime utcMinute,
        DateTime marketNow,
        DateTime utcNow)
    {
        var offset = marketNow - utcNow;
        var roundedOffset = TimeSpan.FromMinutes(Math.Round(offset.TotalMinutes));
        return DateTime.SpecifyKind(
            utcMinute + roundedOffset,
            DateTimeKind.Unspecified);
    }
}
