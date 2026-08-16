namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Text.Json;

public sealed class NasdaqDataException : ReferenceDataException
{
    public NasdaqDataException(
        string code,
        string message,
        Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public static class NasdaqQqqParser
{
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(5);

    public static ReferenceMinuteClose ParseLatestCompleted(
        ReadOnlyMemory<byte> json,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc = null,
        bool allowExpiredQuote = false,
        bool requireNqTradableMinute = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("chart", out var chart)
                || chart.ValueKind != JsonValueKind.Array
                || chart.GetArrayLength() == 0)
            {
                throw new NasdaqDataException(
                    "NoMinuteSeries",
                    "QQQ Nasdaq备用源暂无分钟行情");
            }

            var hasPositivePrice = false;
            var hasCompletedPrice = false;
            DateTime? latestMinuteUtc = null;
            var latestClose = 0m;

            foreach (var point in chart.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Object
                    || !point.TryGetProperty("x", out var timestamp)
                    || !TryReadUnixMilliseconds(timestamp, out var unixMilliseconds)
                    || !point.TryGetProperty("y", out var price)
                    || !TryReadPositiveDecimal(price, out var close))
                {
                    continue;
                }

                hasPositivePrice = true;
                var minuteUtc = UsMarketClock.NasdaqWallClockToUtc(unixMilliseconds);

                if (minuteUtc.AddMinutes(1).Add(CompletionGrace) > nowUtc)
                    continue;

                hasCompletedPrice = true;

                if (requireNqTradableMinute
                    && !NqTradingSessionCalendar.IsOpen(minuteUtc))
                {
                    continue;
                }

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
                    throw new NasdaqDataException(
                        "StaleQuote",
                        "QQQ Nasdaq备用源分钟报价已过期");
                }

                return new ReferenceMinuteClose(
                    "QQQ",
                    latestMinuteUtc.Value,
                    latestClose,
                    "Nasdaq");
            }

            if (!hasPositivePrice)
            {
                throw new NasdaqDataException(
                    "NoValidClose",
                    "QQQ Nasdaq备用源没有有效收盘价");
            }

            if (hasCompletedPrice && lastAcceptedMinuteUtc.HasValue)
            {
                throw new NasdaqDataException(
                    "DuplicateQuote",
                    "QQQ Nasdaq备用源没有新的分钟报价");
            }

            throw new NasdaqDataException(
                "NoCompletedMinute",
                "QQQ Nasdaq备用源没有新的已完成分钟报价");
        }
        catch (NasdaqDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
                                   or InvalidOperationException
                                   or ArgumentOutOfRangeException
                                   or FormatException)
        {
            throw new NasdaqDataException(
                "InvalidJson",
                "Nasdaq QQQ JSON 结构无效",
                ex);
        }
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
