namespace WolfMoss.ATAS.PriceMapping.Core;

public sealed class ReferenceSourcesException : ReferenceDataException
{
    public ReferenceSourcesException(
        string primarySource,
        ReferenceDataException primaryError,
        string fallbackSource,
        ReferenceDataException fallbackError)
        : base(
            "AllSourcesFailed",
            $"{primarySource}: {Diagnostic(primaryError)}；"
            + $"{fallbackSource}: {Diagnostic(fallbackError)}",
            fallbackError)
    {
        PrimarySource = primarySource;
        PrimaryError = primaryError;
        FallbackSource = fallbackSource;
        FallbackError = fallbackError;
    }

    public string PrimarySource { get; }

    public ReferenceDataException PrimaryError { get; }

    public string FallbackSource { get; }

    public ReferenceDataException FallbackError { get; }

    private static string Diagnostic(ReferenceDataException error)
        => error.Code.StartsWith("HTTP", StringComparison.Ordinal)
            ? error.Code
            : error.Message;
}

public static class ReferenceErrorPolicy
{
    public static bool IsDuplicate(Exception exception)
    {
        if (exception is ReferenceSourcesException sources)
        {
            return IsDuplicate(sources.PrimaryError)
                   && IsDuplicate(sources.FallbackError);
        }

        return exception is ReferenceDataException { Code: "DuplicateQuote" };
    }

    public static bool IsTransient(Exception exception)
    {
        if (exception is ReferenceSourcesException sources)
        {
            return IsTransient(sources.PrimaryError)
                   || IsTransient(sources.FallbackError);
        }

        if (exception is not ReferenceDataException referenceError)
            return exception is System.Net.Http.HttpRequestException
                or TaskCanceledException;

        return referenceError.Code is "HTTP429" or "Timeout" or "NetworkError"
               || referenceError.Code.StartsWith("HTTP5", StringComparison.Ordinal);
    }
}

public static class ReferenceQuoteValidator
{
    public static ReferenceMinuteClose Validate(
        ReferenceMinuteClose quote,
        DateTime nowUtc,
        TimeSpan maximumAge,
        DateTime? lastAcceptedMinuteUtc,
        bool allowExpiredQuote)
    {
        if (quote.Close <= 0m || quote.MinuteStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ReferenceDataException(
                "InvalidQuote",
                $"{DisplaySymbol(quote.Symbol)} 分钟报价无效");
        }

        if (lastAcceptedMinuteUtc.HasValue
            && quote.MinuteStartUtc <= lastAcceptedMinuteUtc.Value)
        {
            throw new ReferenceDataException(
                "DuplicateQuote",
                $"{DisplaySymbol(quote.Symbol)} 没有新的分钟报价");
        }

        if (!allowExpiredQuote
            && nowUtc - quote.MinuteStartUtc.AddMinutes(1) > maximumAge)
        {
            throw new ReferenceDataException(
                "StaleQuote",
                $"{DisplaySymbol(quote.Symbol)} 分钟报价已过期");
        }

        return quote;
    }

    private static string DisplaySymbol(string symbol)
        => string.Equals(symbol, "^GSPC", StringComparison.OrdinalIgnoreCase)
            ? "SPX"
            : symbol;
}
