namespace WolfMoss.ATAS.PriceMapping.Core;

public enum DealerHeatmapSessionState
{
    RegularTradingHours,
    NextSession
}

public enum DealerHeatmapState
{
    Disabled,
    MissingApiKey,
    Waiting,
    Fetching,
    Live,
    NextSession,
    Frozen,
    AuthenticationFailed,
    RateLimited
}

public readonly record struct DealerHeatmapTarget(
    string Ticker,
    DateOnly TargetExpiration,
    DealerHeatmapSessionState SessionState);

public readonly record struct DealerHeatmapCell(
    decimal StrikeUsd,
    decimal NetDealerGexUsd);

public sealed record DealerHeatmapFrame(
    string Ticker,
    DateOnly Expiration,
    DateTime MinuteAtUtc,
    decimal SpotUsd,
    IReadOnlyList<DealerHeatmapCell> Cells);

public sealed record DealerHeatmapSnapshot(
    DealerHeatmapTarget? Target,
    DealerHeatmapFrame? Frame,
    DealerHeatmapState State,
    DateTime AttemptUtc,
    DateTime? NextAttemptUtc,
    string Message,
    bool IsFrozen)
{
    public static DealerHeatmapSnapshot Disabled(DateTime utcNow)
        => new(null, null, DealerHeatmapState.Disabled, utcNow, null, "热力图已关闭", false);

    public static DealerHeatmapSnapshot MissingApiKey(DateTime utcNow)
        => new(null, null, DealerHeatmapState.MissingApiKey, utcNow, null, "API key 未配置", false);
}

public sealed class DealerHeatmapDataException : Exception
{
    public DealerHeatmapDataException(
        string code,
        string message,
        DateTime? retryAfterUtc = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        RetryAfterUtc = retryAfterUtc;
    }

    public string Code { get; }

    public DateTime? RetryAfterUtc { get; }
}
