namespace WolfMoss.ATAS.PriceMapping.Core;

public enum FlowSampleDisposition
{
    Accepted, Invalid, FutureSource, BeforeCoverage, OutsideLadder,
    OutsideSession, OutOfOrder, RollingBoundary, Duplicate, ObservationGap, BufferedFuture, FutureBufferOverflow
}

public sealed record FlowReceptionSnapshot(DateTime? SourceUtc, DateTime? ReceivedUtc,
    long Accepted, long Rejected, long Future, long BeforeCoverage, long OutsideLadder,
    long OutsideSession, long OutOfOrder, long Invalid, long RollingBoundary, long Late,
    string LastDisposition)
{
    public long Duplicates { get; init; }
    public long ObservationGaps { get; init; }
    public long Buffered { get; init; }
    public long BufferOverflow { get; init; }
    public DateTime? LastFutureSourceUtc { get; init; }
    public DateTime? LastFutureReceivedUtc { get; init; }
    public double? LastFutureLeadMilliseconds { get; init; }
    public double? MaxFutureLeadMilliseconds { get; init; }
}

// Owned by the existing option-data lock. No per-event allocations, and Off
// bypasses this collector entirely. Counts refer to this indicator's selected scope.
public sealed class FlowReceptionDiagnostics
{
    // Display-only wording. Keep recorded enum names/schema stable.
    public static string DescribeDisposition(string? value) => value switch
    {
        "Accepted" => "已写入共享/本地样本",
        "RollingBoundary" => "观察边界或共享去重",
        "Duplicate" => "重复回调/共享去重（非拒绝）",
        "ObservationGap" => "观察已暂停或关闭",
        "BufferedFuture" => "小幅超前暂存（非拒绝）",
        "FutureBufferOverflow" => "超前暂存队列达到上限",
        "Invalid" => "无效累计值",
        "FutureSource" => "源时间在未来",
        "BeforeCoverage" => "观察开始前",
        "OutsideLadder" => "接收范围外",
        "OutsideSession" => "交易区段外",
        "OutOfOrder" => "倒序",
        _ => "暂无"
    };
    private readonly long[] _counts = new long[12];
    private DateTime? _source, _received;
    private DateTime? _lastFutureSource, _lastFutureReceived;
    private double? _lastFutureLeadMilliseconds, _maxFutureLeadMilliseconds;
    private long _late;
    private FlowSampleDisposition? _last;
    public void Record(DateTime source, DateTime received, FlowSampleDisposition disposition, DateTime? publishedEnd)
    {
        _source = source;
        _received = received;
        _last = disposition;
        _counts[(int)disposition]++;
        if (disposition == FlowSampleDisposition.FutureSource)
        {
            _lastFutureSource = source;
            _lastFutureReceived = received;
            _lastFutureLeadMilliseconds = (source - received).TotalMilliseconds;
            _maxFutureLeadMilliseconds = Math.Max(_maxFutureLeadMilliseconds ?? _lastFutureLeadMilliseconds.Value,
                _lastFutureLeadMilliseconds.Value);
        }
        if (publishedEnd.HasValue && source < publishedEnd && received > publishedEnd) _late++;
    }
    public FlowReceptionSnapshot Read() => new(_source, _received, _counts[0],
        _counts[1] + _counts[2] + _counts[3] + _counts[4] + _counts[5] + _counts[6] + _counts[7] + _counts[9] + _counts[11],
        _counts[2], _counts[3], _counts[4], _counts[5], _counts[6], _counts[1], _counts[7], _late,
        _last?.ToString() ?? "NONE")
    {
        Duplicates = _counts[8], ObservationGaps = _counts[9],
        Buffered = _counts[10], BufferOverflow = _counts[11],
        LastFutureSourceUtc = _lastFutureSource, LastFutureReceivedUtc = _lastFutureReceived,
        LastFutureLeadMilliseconds = _lastFutureLeadMilliseconds, MaxFutureLeadMilliseconds = _maxFutureLeadMilliseconds
    };

    public static string DescribeFutureTimes(FlowReceptionSnapshot? snapshot)
        => snapshot?.LastFutureSourceUtc is { } source && snapshot.LastFutureReceivedUtc is { } received
            ? FormattableString.Invariant($"FLOW 最近未来拒绝 UTC：源 {source:MM-dd HH:mm:ss.fff}；接收 {received:MM-dd HH:mm:ss.fff}")
            : "FLOW 最近未来拒绝 UTC：暂无";

    public static string DescribeFutureLead(FlowReceptionSnapshot? snapshot)
        => snapshot?.LastFutureLeadMilliseconds is { } lead && snapshot.MaxFutureLeadMilliseconds is { } max
            ? FormattableString.Invariant($"FLOW 未来超前 ms：最近 {lead:F4}；累计最大 {max:F4}")
            : "FLOW 未来超前 ms：最近 --；累计最大 --";
}
