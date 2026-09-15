namespace WolfMoss.ATAS.PriceMapping.Core;

// Called by the single publication owner. Monotonic elapsed time distinguishes
// wall-clock adjustments from a slow loop, suspension or a quiet market.
public sealed class RealtimeClockGuard
{
    private DateTime? _previousUtc;
    private long _previousTimestamp;
    public long JumpCount { get; private set; }
    public double LastAdjustmentSeconds { get; private set; }

    public bool Observe(DateTime utc, long timestamp, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var adjustment = _previousUtc.HasValue
            ? (utc - _previousUtc.Value).TotalSeconds - (timestamp - _previousTimestamp) / (double)frequency : 0;
        _previousUtc = utc;
        _previousTimestamp = timestamp;
        if (Math.Abs(adjustment) <= 2) return false;
        JumpCount++;
        LastAdjustmentSeconds = adjustment;
        return true;
    }
}
