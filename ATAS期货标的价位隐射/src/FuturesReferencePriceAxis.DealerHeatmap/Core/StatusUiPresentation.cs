namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Drawing;
using System.Globalization;

internal static class StatusUiPresentation
{
    public const int CountdownHeight = 26;
    public const int CountdownGap = 6;

    // Same configured width and anchor for the panel and independent countdown.
    public static Rectangle Place(Rectangle region, int reservedWidth, int configuredWidth,
        int offsetX, int offsetY, int panelHeight, bool countdown)
    {
        var width = Math.Max(0, Math.Min(configuredWidth, region.Width - reservedWidth - 16));
        var totalHeight = panelHeight + (countdown ? CountdownHeight + (panelHeight > 0 ? CountdownGap : 0) : 0);
        var left = Math.Clamp(region.Left + reservedWidth + 8 + offsetX, region.Left + 4,
            Math.Max(region.Left + 4, region.Right - width - 4));
        var top = Math.Clamp(region.Top + 8 + offsetY, region.Top + 4,
            Math.Max(region.Top + 4, region.Bottom - totalHeight - 4));
        return new(left, top, width, panelHeight);
    }

    public static string[] Wrap(IReadOnlyList<string> source, int width, int maxRows, Func<string, int> measure)
    {
        if (maxRows <= 0 || width <= 0) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var line in source)
        {
            var remaining = line;
            do
            {
                if (result.Count == maxRows)
                {
                    result[^1] = "… 信息未显示完";
                    return result.ToArray();
                }
                if (measure(remaining) <= width) { result.Add(remaining); break; }
                var low = 1; var high = remaining.Length;
                while (low < high)
                {
                    var middle = (low + high + 1) / 2;
                    if (measure(remaining[..middle]) <= width) low = middle; else high = middle - 1;
                }
                // Don't split a surrogate pair. No network/API content is rendered as markup.
                if (low < remaining.Length && char.IsHighSurrogate(remaining[low - 1])) low = Math.Max(1, low - 1);
                result.Add(remaining[..low]);
                remaining = remaining[low..];
            } while (remaining.Length > 0);
        }
        return result.ToArray();
    }
}

internal sealed record FlowCountdownContext(string Ticker, DateOnly Expiration,
    IReadOnlyList<OptionTradingSegment> Segments);

internal static class FlowCountdownPresentation
{
    public static string Text(DateTime now, OptionFlowBucketMode mode, int minutes,
        IReadOnlyList<OptionTradingSegment>? segments, DateTime? nextAtmSwitch, OptionDataStatus status)
    {
        var prefix = $"Flow {minutes}m";
        if (status is OptionDataStatus.Frozen or OptionDataStatus.NoPermission or OptionDataStatus.LineLimit)
            return $"{prefix}：暂停（{status}）";
        if (status == OptionDataStatus.NoContracts) return $"{prefix}：无合约";
        if (segments == null || segments.Count == 0) return $"{prefix}：等待交易时段";
        var segment = default(OptionTradingSegment);
        foreach (var item in segments) if (item.Contains(now)) { segment = item; break; }
        if (segment == default) return $"{prefix}：休市";
        DateTime deadline;
        if (mode == OptionFlowBucketMode.Rolling)
        {
            if (!nextAtmSwitch.HasValue || nextAtmSwitch <= now) return $"{prefix} 滚动：等待 ATM 换档";
            deadline = nextAtmSwitch.Value < segment.EndUtc ? nextAtmSwitch.Value : segment.EndUtc;
            prefix += deadline == segment.EndUtc ? " 休市" : " ATM";
        }
        else
        {
            deadline = OptionFlowAggregation.GetFixedBucketStart(now, segment, minutes).AddMinutes(minutes);
            if (deadline > segment.EndUtc) return $"{prefix}：区段尾部不足完整桶";
            prefix += " 桶";
        }
        var seconds = (int)Math.Ceiling((deadline - now).TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix} {seconds / 60:00}:{seconds % 60:00}");
    }
}
