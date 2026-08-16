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

        var easternOffset = UsMarketClock.GetUtcOffset(
            UsMarketClock.EasternTimeZone,
            utcNow);
        var centralOffset = UsMarketClock.GetUtcOffset(
            UsMarketClock.CentralTimeZone,
            utcNow);

        if (roundedOffset == easternOffset)
        {
            return ConvertUsingTimeZone(
                utcMinute,
                UsMarketClock.EasternTimeZone);
        }

        if (roundedOffset == centralOffset)
        {
            return ConvertUsingTimeZone(
                utcMinute,
                UsMarketClock.CentralTimeZone);
        }

        return DateTime.SpecifyKind(
            utcMinute + roundedOffset,
            DateTimeKind.Unspecified);
    }

    private static DateTime ConvertUsingTimeZone(
        DateTime utcMinute,
        TimeZoneInfo timeZone)
    {
        var normalizedUtc = DateTime.SpecifyKind(utcMinute, DateTimeKind.Utc);
        var marketTime = TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, timeZone);
        return DateTime.SpecifyKind(marketTime, DateTimeKind.Unspecified);
    }
}
