namespace WolfMoss.ATAS.PriceMapping.Core;

public enum DealerGexState
{
    Disabled,
    MissingApiKey,
    Waiting,
    Fetching,
    Live,
    Closed,
    Frozen,
    AuthenticationFailed,
    RateLimited
}

public readonly record struct DealerGexNode(
    decimal StrikeUsd,
    decimal NetGexUsd,
    string NodeType,
    int Rank,
    decimal RelativeStrength);

public sealed record DealerGexSummary(
    decimal? TotalGexUsd,
    decimal? KingStrikeUsd,
    decimal? GammaFlipUsd,
    decimal? CallWallStrikeUsd,
    decimal? PutWallStrikeUsd,
    decimal? MajorPositiveStrikeUsd,
    decimal? MajorNegativeStrikeUsd);

public sealed record DealerGexFrame(
    string Ticker,
    DateTime SnapshotAtUtc,
    DateOnly SessionDateEt,
    string ApiState,
    decimal SpotUsd,
    IReadOnlyList<DealerGexNode> Nodes,
    DealerGexSummary Summary);

public sealed record DealerGexSnapshot(
    string? Ticker,
    DealerGexFrame? Frame,
    DealerGexState State,
    DateTime AttemptUtc,
    DateTime? NextAttemptUtc,
    string Message,
    bool IsFrozen)
{
    public static DealerGexSnapshot Disabled(DateTime utcNow)
        => new(null, null, DealerGexState.Disabled, utcNow, null, "Dealer GEX 已关闭", false);

    public static DealerGexSnapshot MissingApiKey(DateTime utcNow)
        => new(null, null, DealerGexState.MissingApiKey, utcNow, null, "API key 未配置", false);
}

public sealed class DealerGexDataException : Exception
{
    public DealerGexDataException(
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
