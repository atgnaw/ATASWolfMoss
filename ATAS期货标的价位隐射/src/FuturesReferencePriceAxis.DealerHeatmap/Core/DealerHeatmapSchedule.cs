namespace WolfMoss.ATAS.PriceMapping.Core;

public static class DealerHeatmapSchedule
{
    public static readonly TimeSpan PublicationDelay = TimeSpan.FromSeconds(20);

    public static DateTime GetSampleBucketUtc(DateTime utcTime)
    {
        var normalized = utcTime.Kind == DateTimeKind.Utc
            ? utcTime
            : DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        var ticks = normalized.Ticks - normalized.Ticks % TimeSpan.FromMinutes(5).Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public static DateTime CalculateNextAttemptUtc(
        DateTime utcNow,
        int rthRefreshMinutes,
        int offHoursRefreshMinutes,
        DateTime? retryNotBeforeUtc = null)
    {
        var now = utcNow.Kind == DateTimeKind.Utc
            ? utcNow
            : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        DateTime candidate;

        if (NyseTradingCalendar.IsRegularTradingHours(now))
        {
            var interval = NormalizeMinutes(rthRefreshMinutes, 5, 60);
            var eastern = UsMarketClock.ToEastern(now);
            var sessionOpen = eastern.Date + NyseTradingCalendar.RegularOpen;
            var elapsed = eastern - sessionOpen - PublicationDelay;
            var completedIntervals = elapsed < TimeSpan.Zero
                ? 0
                : (int)Math.Floor(elapsed.TotalMinutes / interval) + 1;
            var boundaryIndex = Math.Max(1, completedIntervals);
            var nextEastern = sessionOpen
                              + TimeSpan.FromMinutes(boundaryIndex * interval)
                              + PublicationDelay;
            var finalEastern = eastern.Date
                               + NyseTradingCalendar.GetRegularClose(eastern.Date)
                               + PublicationDelay;

            if (nextEastern > finalEastern)
                nextEastern = finalEastern;

            candidate = NyseTradingCalendar.EasternToUtc(nextEastern);

            if (candidate <= now)
                candidate = NyseTradingCalendar.GetNextSessionTransitionUtc(now);
        }
        else
        {
            var interval = NormalizeMinutes(offHoursRefreshMinutes, 5, 1440);
            var intervalCandidate = now + TimeSpan.FromMinutes(interval);
            var transition = NyseTradingCalendar.GetNextSessionTransitionUtc(now);
            candidate = intervalCandidate <= transition ? intervalCandidate : transition;
        }

        if (retryNotBeforeUtc.HasValue && candidate < retryNotBeforeUtc.Value)
            candidate = retryNotBeforeUtc.Value;

        return candidate;
    }

    public static int NormalizeMinutes(int value, int minimum, int maximum)
    {
        var clamped = Math.Clamp(value, minimum, maximum);
        return Math.Min(maximum, ((clamped + 4) / 5) * 5);
    }
}
