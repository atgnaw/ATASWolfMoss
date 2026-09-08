namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public static class IbTradingHoursParser
{
    private static readonly string[] Formats = ["yyyyMMdd:HHmm", "yyyyMMdd:HHmm:ss"];

    public static IReadOnlyList<OptionTradingSegment> ParseEastern(string? tradingHours, bool includeGlobalTradingHours)
        => Parse(tradingHours, "America/New_York", includeGlobalTradingHours);

    public static IReadOnlyList<OptionTradingSegment> Parse(string? tradingHours, string? timeZoneId, bool includeGlobalTradingHours)
    {
        var zone = ResolveTimeZone(timeZoneId);
        if (string.IsNullOrWhiteSpace(tradingHours) || tradingHours.Length > 65_536 || zone == null)
            return Array.Empty<OptionTradingSegment>();
        var segments = new List<OptionTradingSegment>();
        foreach (var raw in tradingHours.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Contains("CLOSED", StringComparison.OrdinalIgnoreCase) || part.Length < 9 || part[8] != ':') continue;
            var datePrefix = part[..9];
            foreach (var range in part.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var endpoints = range.Trim().Split('-');
                if (endpoints.Length != 2) continue;
                var left = Expand(endpoints[0].Trim(), datePrefix);
                var right = Expand(endpoints[1].Trim(), left.Length >= 9 ? left[..9] : datePrefix);
                if (!TryParse(left, zone, out var start) || !TryParse(right, zone, out var end)
                    || end <= start || end - start > TimeSpan.FromDays(2)) continue;
                if (includeGlobalTradingHours) segments.Add(new(start, end));
                else
                {
                    // Intersect each actual open range with RTH, including overnight input.
                    var lastDate = UsMarketClock.ToEastern(end).Date;
                    for (var day = UsMarketClock.ToEastern(start).Date; day <= lastDate; day = day.AddDays(1))
                    {
                        if (!NyseTradingCalendar.IsTradingDay(day)) continue;
                        var rthStart = NyseTradingCalendar.EasternToUtc(day + NyseTradingCalendar.RegularOpen);
                        var rthEnd = NyseTradingCalendar.EasternToUtc(day + NyseTradingCalendar.GetRegularClose(day));
                        var a = start > rthStart ? start : rthStart;
                        var b = end < rthEnd ? end : rthEnd;
                        if (b > a) segments.Add(new(a, b));
                    }
                }
            }
        }
        // Merge true overlaps/duplicates only. Adjacent sessions remain distinct anchors.
        var normalized = new List<OptionTradingSegment>();
        foreach (var segment in segments.OrderBy(static s => s.StartUtc))
        {
            if (normalized.Count > 0 && segment.StartUtc < normalized[^1].EndUtc)
            {
                var last = normalized[^1];
                if (segment.EndUtc > last.EndUtc) normalized[^1] = last with { EndUtc = segment.EndUtc };
            }
            else normalized.Add(segment);
        }
        return normalized.ToArray();
    }

    private static string Expand(string value, string datePrefix)
        => value.Length >= 9 && value[8] == ':' ? value : datePrefix + value;

    private static bool TryParse(string text, TimeZoneInfo zone, out DateTime utc)
    {
        utc = default;
        DateTime local;
        if (text.EndsWith(":2400", StringComparison.Ordinal)
            && DateTime.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            if (day == DateTime.MaxValue.Date) return false;
            local = day.AddDays(1);
        }
        else if (!DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // No guessing through DST's missing/duplicated wall-clock hour.
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return false;
        utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return true;
    }

    private static TimeZoneInfo? ResolveTimeZone(string? timeZoneId)
    {
        var normalized = timeZoneId?.Trim();
        var id = normalized?.ToUpperInvariant() switch
        {
            "US/CENTRAL" or "AMERICA/CHICAGO" or "CST6CDT" or "CST" => "Central Standard Time",
            "US/EASTERN" or "AMERICA/NEW_YORK" or "EST5EDT" or "EST" => "Eastern Standard Time",
            _ => normalized
        };
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
