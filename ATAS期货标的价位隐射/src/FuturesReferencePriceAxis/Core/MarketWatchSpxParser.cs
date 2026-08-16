namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Text.Json;

public sealed class MarketWatchDataException : ReferenceDataException
{
    public MarketWatchDataException(
        string code,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public static class MarketWatchSpxParser
{
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(5);

    public static ReferenceMinuteClose ParseLatestCompleted(
        ReadOnlyMemory<byte> json,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc = null,
        bool allowExpiredQuote = true)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("TimeInfo", out var timeInfo)
                || timeInfo.ValueKind != JsonValueKind.Object
                || !TryGetNonEmptyArray(timeInfo, "Ticks", out var ticks)
                || !TryGetNonEmptyArray(root, "Series", out var series)
                || !TryFindDataPoints(series, out var dataPoints))
            {
                throw new MarketWatchDataException(
                    "NoMinuteSeries",
                    "SPX MarketWatch备用源暂无分钟行情");
            }

            var length = Math.Min(ticks.GetArrayLength(), dataPoints.GetArrayLength());
            var hasPositivePrice = false;
            var hasCompletedPrice = false;
            DateTime? latestMinuteUtc = null;
            var latestClose = 0m;

            for (var i = 0; i < length; i++)
            {
                if (!TryReadUnixMilliseconds(ticks[i], out var unixMilliseconds)
                    || !TryReadPositiveDecimal(dataPoints[i], out var close))
                {
                    continue;
                }

                hasPositivePrice = true;
                var minuteUtc = DateTimeOffset
                    .FromUnixTimeMilliseconds(unixMilliseconds)
                    .UtcDateTime;

                if (minuteUtc.AddMinutes(1).Add(CompletionGrace) > nowUtc)
                    continue;

                hasCompletedPrice = true;

                if (lastAcceptedMinuteUtc.HasValue
                    && minuteUtc <= lastAcceptedMinuteUtc.Value)
                {
                    continue;
                }

                if (!latestMinuteUtc.HasValue || minuteUtc > latestMinuteUtc.Value)
                {
                    latestMinuteUtc = minuteUtc;
                    latestClose = close;
                }
            }

            if (latestMinuteUtc.HasValue)
            {
                if (!allowExpiredQuote
                    && nowUtc - latestMinuteUtc.Value.AddMinutes(1) > maximumAge)
                {
                    throw new MarketWatchDataException(
                        "StaleQuote",
                        "SPX MarketWatch备用源分钟报价已过期");
                }

                return new ReferenceMinuteClose(
                    "^GSPC",
                    latestMinuteUtc.Value,
                    latestClose,
                    "MarketWatch");
            }

            if (!hasPositivePrice)
            {
                throw new MarketWatchDataException(
                    "NoValidClose",
                    "SPX MarketWatch备用源没有有效收盘价");
            }

            if (hasCompletedPrice && lastAcceptedMinuteUtc.HasValue)
            {
                throw new MarketWatchDataException(
                    "DuplicateQuote",
                    "SPX MarketWatch备用源没有新的分钟报价");
            }

            throw new MarketWatchDataException(
                "NoCompletedMinute",
                "SPX MarketWatch备用源没有新的已完成分钟报价");
        }
        catch (MarketWatchDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
                                   or InvalidOperationException
                                   or ArgumentOutOfRangeException
                                   or FormatException)
        {
            throw new MarketWatchDataException(
                "InvalidJson",
                "MarketWatch SPX JSON 结构无效",
                ex);
        }
    }

    private static bool TryFindDataPoints(
        JsonElement series,
        out JsonElement dataPoints)
    {
        foreach (var item in series.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("DataPoints", out dataPoints)
                && dataPoints.ValueKind == JsonValueKind.Array
                && dataPoints.GetArrayLength() > 0)
            {
                return true;
            }
        }

        dataPoints = default;
        return false;
    }

    private static bool TryGetNonEmptyArray(
        JsonElement parent,
        string propertyName,
        out JsonElement array)
    {
        array = default;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(propertyName, out array)
               && array.ValueKind == JsonValueKind.Array
               && array.GetArrayLength() > 0;
    }

    private static bool TryReadUnixMilliseconds(
        JsonElement value,
        out long unixMilliseconds)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out unixMilliseconds);

        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out unixMilliseconds);
        }

        unixMilliseconds = 0L;
        return false;
    }

    private static bool TryReadPositiveDecimal(
        JsonElement value,
        out decimal result)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 0)
                return TryReadPositiveDecimal(value[0], out result);

            result = 0m;
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out result))
        {
            return result > 0m;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            result = 0m;
            return false;
        }

        var text = value.GetString()?.Trim().TrimStart('$').Replace(",", string.Empty);
        return decimal.TryParse(
                   text,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out result)
               && result > 0m;
    }
}
