namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;
using System.Text.Json;

public sealed class YahooDataException : Exception
{
    public YahooDataException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class YahooChartParser
{
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(5);

    public static ReferenceMinuteClose ParseLatestCompleted(
        ReadOnlyMemory<byte> json,
        string symbol,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc = null,
        bool allowExpiredQuote = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("chart", out var chart)
                || chart.ValueKind != JsonValueKind.Object)
            {
                throw new YahooDataException(
                    "InvalidJson",
                    "Yahoo JSON 结构无效");
            }

            if (chart.TryGetProperty("error", out var error)
                && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                throw new YahooDataException("YahooError", error.ToString());
            }

            if (!TryGetNonEmptyArray(chart, "result", out var result))
                throw new YahooDataException("EmptyResult", "Yahoo 返回空结果");

            var first = result[0];
            var displaySymbol = DisplaySymbol(symbol);

            if (first.ValueKind != JsonValueKind.Object
                || !TryGetNonEmptyArray(first, "timestamp", out var timestamps))
            {
                throw new YahooDataException(
                    "NoMinuteSeries",
                    $"{displaySymbol} 当前无分钟行情（可能未开盘）");
            }

            if (!first.TryGetProperty("indicators", out var indicators)
                || indicators.ValueKind != JsonValueKind.Object
                || !TryGetNonEmptyArray(indicators, "quote", out var quotes)
                || quotes[0].ValueKind != JsonValueKind.Object
                || !TryGetNonEmptyArray(quotes[0], "close", out var closes))
            {
                throw new YahooDataException(
                    "NoMinuteSeries",
                    $"{displaySymbol} 当前无分钟行情（可能未开盘）");
            }

            var length = Math.Min(timestamps.GetArrayLength(), closes.GetArrayLength());
            var hasPositivePrice = false;
            var hasCompletedPrice = false;

            for (var i = length - 1; i >= 0; i--)
            {
                if (timestamps[i].ValueKind != JsonValueKind.Number
                    || closes[i].ValueKind != JsonValueKind.Number
                    || !timestamps[i].TryGetInt64(out var unixSeconds))
                {
                    continue;
                }

                if (!TryReadPositiveDecimal(closes[i], out var close))
                    continue;

                hasPositivePrice = true;
                var minuteUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

                if (minuteUtc.AddMinutes(1).Add(CompletionGrace) > nowUtc)
                    continue;

                hasCompletedPrice = true;

                if (lastAcceptedMinuteUtc.HasValue && minuteUtc <= lastAcceptedMinuteUtc.Value)
                    continue;

                if (!allowExpiredQuote
                    && nowUtc - minuteUtc.AddMinutes(1) > maximumAge)
                {
                    throw new YahooDataException(
                        "StaleQuote",
                        $"{displaySymbol} 分钟报价已过期");
                }

                return new ReferenceMinuteClose(symbol, minuteUtc, close);
            }

            if (!hasPositivePrice)
            {
                throw new YahooDataException(
                    "NoValidClose",
                    $"{displaySymbol} 分钟序列没有有效收盘价");
            }

            if (hasCompletedPrice && lastAcceptedMinuteUtc.HasValue)
            {
                throw new YahooDataException(
                    "DuplicateQuote",
                    $"{displaySymbol} 没有新的分钟报价");
            }

            throw new YahooDataException(
                "NoCompletedMinute",
                $"{displaySymbol} 没有新的已完成分钟报价");
        }
        catch (YahooDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException
                                   or KeyNotFoundException
                                   or InvalidOperationException
                                   or ArgumentOutOfRangeException
                                   or FormatException)
        {
            throw new YahooDataException(
                "InvalidJson",
                "Yahoo JSON 结构无效",
                ex);
        }
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

    private static string DisplaySymbol(string symbol)
        => string.Equals(symbol, "^GSPC", StringComparison.OrdinalIgnoreCase)
            ? "SPX"
            : symbol;

    private static bool TryReadPositiveDecimal(JsonElement value, out decimal result)
    {
        if (value.TryGetDecimal(out result))
            return result > 0m;

        return decimal.TryParse(
                   value.GetRawText(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out result)
               && result > 0m;
    }
}
