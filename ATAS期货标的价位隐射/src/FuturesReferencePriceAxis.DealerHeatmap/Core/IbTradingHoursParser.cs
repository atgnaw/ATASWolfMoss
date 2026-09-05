namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public static class IbTradingHoursParser
{
    private static readonly string[] Formats =
    [
        "yyyyMMdd:HHmm",
        "yyyyMMdd:HHmm:ss"
    ];

    public static IReadOnlyList<OptionTradingSegment> ParseEastern(
        string? tradingHours,
        bool includeGlobalTradingHours)
        => Parse(tradingHours, "America/New_York", includeGlobalTradingHours);

    public static IReadOnlyList<OptionTradingSegment> Parse(
        string? tradingHours,
        string? timeZoneId,
        bool includeGlobalTradingHours)
    {
        if (string.IsNullOrWhiteSpace(tradingHours))
            return Array.Empty<OptionTradingSegment>();

        var segments = new List<OptionTradingSegment>();

        foreach (var part in tradingHours.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains("CLOSED", StringComparison.OrdinalIgnoreCase))
                continue;

            var dash = part.IndexOf('-');

            if (dash <= 0 || dash >= part.Length - 1)
                continue;

            var left = part[..dash];
            var right = part[(dash + 1)..];

            if (!TryParse(left, timeZoneId, out var startUtc)
                || !TryParse(right, timeZoneId, out var endUtc)
                || endUtc <= startUtc)
            {
                continue;
            }

            if (!includeGlobalTradingHours)
            {
                var easternDate = UsMarketClock.ToEastern(startUtc).Date;
                var rthStart = NyseTradingCalendar.EasternToUtc(
                    easternDate + NyseTradingCalendar.RegularOpen);
                var rthEnd = NyseTradingCalendar.EasternToUtc(
                    easternDate + NyseTradingCalendar.GetRegularClose(easternDate));
                startUtc = startUtc > rthStart ? startUtc : rthStart;
                endUtc = endUtc < rthEnd ? endUtc : rthEnd;

                if (endUtc <= startUtc)
                    continue;
            }

            segments.Add(new OptionTradingSegment(startUtc, endUtc));
        }

        return segments
            .Distinct()
            .OrderBy(static segment => segment.StartUtc)
            .ToArray();
    }

    private static bool TryParse(string text, string? timeZoneId, out DateTime utc)
    {
        utc = default;

        if (!DateTime.TryParseExact(text.Trim(), Formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var eastern))
        {
            return false;
        }

        var zone = ResolveTimeZone(timeZoneId);
        eastern = DateTime.SpecifyKind(eastern, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(eastern))
            return false;

        utc = TimeZoneInfo.ConvertTimeToUtc(eastern, zone);
        return true;
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var normalized = timeZoneId?.Trim();
        var windowsId = normalized?.ToUpperInvariant() switch
        {
            "US/CENTRAL" or "AMERICA/CHICAGO" or "CST6CDT" or "CST"
                => "Central Standard Time",
            "US/EASTERN" or "AMERICA/NEW_YORK" or "EST5EDT" or "EST"
                => "Eastern Standard Time",
            _ => normalized
        };

        if (!string.IsNullOrWhiteSpace(windowsId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return UsMarketClock.EasternTimeZone;
    }
}
