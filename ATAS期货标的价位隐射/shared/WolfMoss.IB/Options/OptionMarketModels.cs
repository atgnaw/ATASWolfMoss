// Source-shared module: compiled privately into each consuming plugin.
namespace WolfMoss.ATAS.PriceMapping.Core;

public enum OptionFlowBucketMode
{
    PreviousCompletedFixed,
    Rolling
}

public enum OptionFlowTradeScope
{
    RegularTrades,
    AllTimeAndSales
}

public enum OptionRight
{
    Call,
    Put
}

public enum OptionDataStatus
{
    Disabled,
    Connecting,
    WaitingChain,
    WaitingOpenInterest,
    Warming,
    Live,
    Daily,
    Partial,
    Closed,
    Frozen,
    Delayed,
    NoPermission,
    LineLimit,
    NoContracts
}

public sealed record OptionUnderlyingProfile(
    string Ticker,
    string SecurityType,
    string Exchange,
    string Currency,
    string PreferredTradingClass,
    bool IncludeGlobalTradingHours)
{
    public static OptionUnderlyingProfile Qqq { get; } =
        new("QQQ", "STK", "SMART", "USD", "QQQ", false);

    public static OptionUnderlyingProfile Spx { get; } =
        new("SPX", "IND", "CBOE", "USD", "SPXW", true);

    public static bool TryResolve(string ticker, out OptionUnderlyingProfile profile)
    {
        if (string.Equals(ticker, "QQQ", StringComparison.OrdinalIgnoreCase))
        {
            profile = Qqq;
            return true;
        }

        if (string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase))
        {
            profile = Spx;
            return true;
        }

        profile = null!;
        return false;
    }
}

public readonly record struct OptionContractDescriptor(
    long ConId,
    string Ticker,
    DateOnly Expiration,
    decimal StrikeUsd,
    OptionRight Right,
    string TradingClass,
    string Exchange,
    decimal Multiplier,
    string TradingHours,
    string TradingTimeZoneId = "America/New_York");

public readonly record struct OptionContractCoverage(
    int ExpectedContractCount,
    int ResolvedContractCount,
    int ActiveStrikeCount)
{
    public bool IsComplete
        => ExpectedContractCount > 0
           && ResolvedContractCount == ExpectedContractCount;

    public static OptionContractCoverage Calculate(
        IReadOnlyList<decimal> requestedStrikes,
        IReadOnlyList<OptionContractDescriptor> resolvedContracts)
    {
        var resolved = resolvedContracts
            .Select(static contract => contract.ConId)
            .Distinct()
            .Count();
        var activeStrikes = resolvedContracts
            .Select(static contract => contract.StrikeUsd)
            .Distinct()
            .Count();

        return new OptionContractCoverage(
            checked(requestedStrikes.Count * 2),
            resolved,
            activeStrikes);
    }
}

public enum OptionFlowCoverage
{
    Full,
    Partial,
    NoEvents,
    NoData
}

public sealed record OptionStrikeLadder(
    string Ticker,
    DateOnly Expiration,
    decimal AtmStrikeUsd,
    IReadOnlyList<decimal> Strikes,
    DateTime CreatedUtc);

public readonly record struct OptionStrikeRow(
    decimal StrikeUsd,
    long? CallOpenInterest,
    long? PutOpenInterest,
    decimal? CallPremium,
    decimal? PutPremium,
    long? CallVolume,
    long? PutVolume,
    DateTime? CallFlowObservedStartUtc = null,
    DateTime? PutFlowObservedStartUtc = null,
    bool CallFlowIsPartial = false,
    bool PutFlowIsPartial = false,
    OptionFlowCoverage? CallFlowCoverage = null,
    OptionFlowCoverage? PutFlowCoverage = null,
    bool CallFlowRetained = false,
    bool PutFlowRetained = false,
    decimal? FlowLowerBoundUsd = null,
    decimal? FlowUpperBoundUsd = null);

public sealed record OptionOpenInterestSnapshot(
    string? Ticker,
    DateOnly? Expiration,
    decimal? AtmStrikeUsd,
    IReadOnlyList<OptionStrikeRow> Rows,
    OptionDataStatus Status,
    DateTime ReceivedUtc,
    string Message,
    int RequestedStrikeCount,
    int ActiveStrikeCount)
{
    // OI-only metadata: do not enlarge every hot-path Flow row for these timestamps.
    public IReadOnlyDictionary<(decimal Strike, OptionRight Right), DateTime?> ReceivedByStrike { get; init; }
        = new Dictionary<(decimal, OptionRight), DateTime?>();

    public static OptionOpenInterestSnapshot Disabled(DateTime utcNow)
        => new(null, null, null, Array.Empty<OptionStrikeRow>(),
            OptionDataStatus.Disabled, utcNow, "Option OI 已关闭", 0, 0);

    public static OptionOpenInterestSnapshot Waiting(DateTime utcNow, string message)
        => new(null, null, null, Array.Empty<OptionStrikeRow>(),
            OptionDataStatus.Connecting, utcNow, message, 0, 0);
}

public sealed record OptionFlowSnapshot(
    string? Ticker,
    DateOnly? Expiration,
    decimal? AtmStrikeUsd,
    IReadOnlyList<OptionStrikeRow> Rows,
    OptionDataStatus Status,
    DateTime? BucketStartUtc,
    DateTime? BucketEndUtc,
    DateTime ReceivedUtc,
    string Message,
    OptionFlowBucketMode BucketMode,
    OptionFlowTradeScope TradeScope,
    int IntervalMinutes,
    int RequestedStrikeCount,
    int ActiveStrikeCount,
    DateTime? AtmLockedUntilUtc = null,
    int RetainedStrikeCount = 0)
{
    public static OptionFlowSnapshot Disabled(
        DateTime utcNow,
        OptionFlowBucketMode mode,
        OptionFlowTradeScope scope,
        int intervalMinutes)
        => new(null, null, null, Array.Empty<OptionStrikeRow>(),
            OptionDataStatus.Disabled, null, null, utcNow,
            "Option Flow 已关闭", mode, scope, intervalMinutes, 0, 0);

    public static OptionFlowSnapshot Waiting(
        DateTime utcNow,
        string message,
        OptionFlowBucketMode mode,
        OptionFlowTradeScope scope,
        int intervalMinutes)
        => new(null, null, null, Array.Empty<OptionStrikeRow>(),
            OptionDataStatus.Connecting, null, null, utcNow,
            message, mode, scope, intervalMinutes, 0, 0);
}

public readonly record struct OptionCumulativeSample(
    long ConId,
    DateTime SampleUtc,
    long TotalVolume,
    decimal Vwap,
    decimal Multiplier,
    decimal? LastTradePrice = null,
    long? LastTradeSize = null)
{
    public decimal CumulativePremium => Vwap * TotalVolume * Multiplier;

    public bool HasValidCumulativeValue()
    {
        if (ConId <= 0 || TotalVolume < 0 || Vwap < 0 || Multiplier <= 0) return false;
        try { _ = CumulativePremium; return true; }
        catch (OverflowException) { return false; }
    }

    public bool TryGetLastTradeValue(out OptionIntervalValue value)
    {
        value = OptionIntervalValue.Zero;

        if (LastTradePrice is not >= 0m
            || LastTradeSize is not > 0
            || Multiplier <= 0m)
        {
            return false;
        }

        decimal premium;
        try { premium = LastTradePrice.Value * LastTradeSize.Value * Multiplier; }
        catch (OverflowException) { return false; }
        value = new OptionIntervalValue(
            LastTradeSize.Value,
            premium,
            SampleUtc,
            true);
        return true;
    }
}

public readonly record struct OptionIntervalValue(
    long Volume,
    decimal Premium,
    DateTime? ObservedStartUtc = null,
    bool IsPartial = false)
{
    public static OptionIntervalValue Zero => new(0, 0m);
}

public readonly record struct OptionTradingSegment(DateTime StartUtc, DateTime EndUtc)
{
    public bool Contains(DateTime utcTime)
        => utcTime >= StartUtc && utcTime < EndUtc;
}
