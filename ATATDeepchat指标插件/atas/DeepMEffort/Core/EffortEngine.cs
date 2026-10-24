namespace WolfMoss.ATAS.DeepMEffort.Core;

internal sealed record EffortConfig
{
    public double MinDeltaPercent { get; init; } = 10;
    public double MaxDeltaPercent { get; init; } = 40;
    public double MaxDeltaEffort { get; init; } = 14;
    public long MaxTimeSeconds { get; init; } = 45;
    public int MinimumBars { get; init; } = 2;
    public int EmaLength { get; init; } = 20;
    public int MaxZoneExtensions { get; init; } = 20;
    public int FullRangeMaxBars { get; init; } = 1;
    public double EntryZonePercent { get; init; } = 50;

    public void Validate()
    {
        if (!Percent(MinDeltaPercent) || !Percent(MaxDeltaPercent) || !Percent(EntryZonePercent)
            || (MaxDeltaPercent != 0 && MinDeltaPercent > MaxDeltaPercent)
            || !double.IsFinite(MaxDeltaEffort) || MaxDeltaEffort < 0
            || MaxTimeSeconds < 0 || MinimumBars is < 1 or > 4096 || EmaLength is < 1 or > 4096
            || MaxZoneExtensions is < 0 or > 4096 || FullRangeMaxBars is < 0 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(EffortConfig), "Invalid Effort configuration.");
    }

    private static bool Percent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;
}

internal readonly record struct EffortBar(int Index, double Open, double High, double Low, double Close,
    long OpenTicks, long CloseTicks, long AskVolume, long BidVolume, long TotalVolume,
    long ElapsedMicroseconds, bool HasKnownAggressor = true, bool HasValidElapsed = true);

internal readonly record struct EffortMetrics(long Delta, double DeltaPercent, long Effort,
    long ElapsedSeconds, bool HasVolume, bool IsEligible);

internal enum StopReason { None, Boundary, Age, Session, Contract }

internal readonly record struct EffortZone(long Id, int CreatedIndex, int EndIndex, double Lower,
    double Upper, bool IsBullish, bool StopsOnAbove, bool IsActive = true, StopReason StopReason = StopReason.None);

internal readonly record struct PendingSegment(double High, double Low, long BarCount, bool IsBullish);

/// <summary>
/// Platform-independent port of deep_m_effort/engine.cpp and zone_lifecycle.cpp.
/// The caller serializes access; repeated ticks must never be submitted as closed bars.
/// Only active zones are visited per close. Completed zones remain available for historical drawing.
/// </summary>
internal sealed class EffortEngine
{
    private readonly EffortConfig _config;
    private readonly Queue<(double High, double Low)> _recent = new();
    private readonly List<int> _active = [];
    private readonly List<EffortZone> _zones = [];
    private long _consecutive;
    private bool _previousIsBullish;
    private PendingSegment? _pending;
    private long _observedBars;
    private long _nextZoneId = 1;

    public EffortEngine(EffortConfig config, int firstBar = 0)
    {
        config.Validate();
        if (firstBar < 0 || firstBar == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(firstBar));
        _config = config;
        NextBarIndex = firstBar;
    }

    public int NextBarIndex { get; private set; }
    public double? Ema { get; private set; }
    public IReadOnlyList<EffortZone> Zones => _zones;
    public PendingSegment? Pending => _pending;
    public long ConsecutiveBars => _consecutive;

    public EffortMetrics CloseBar(EffortBar bar)
    {
        ValidateBar(bar);
        var metrics = Measure(bar);
        if (_recent.Count == _config.MinimumBars) _recent.Dequeue();
        _recent.Enqueue((bar.High, bar.Low));

        if (!bar.HasKnownAggressor || !bar.HasValidElapsed)
        {
            // Missing data must discard a pending segment, not confirm it.
            _consecutive = 0;
            _previousIsBullish = false;
            _pending = null;
        }
        else if (metrics.HasVolume)
        {
            _consecutive = !metrics.IsEligible ? 0
                : (metrics.Delta >= 0) != _previousIsBullish ? 1 : checked(_consecutive + 1);
            if (metrics.IsEligible) _previousIsBullish = metrics.Delta >= 0;
            if (_consecutive < _config.MinimumBars)
            {
                if (_pending is { } completed) CreateZone(completed, bar);
                _pending = null;
            }
            else
            {
                // Preserve minimumBars=1 direction changes and the original high=0 seed.
                var pending = _pending ?? new PendingSegment(0, double.MaxValue, _consecutive, metrics.Delta >= 0);
                if (_pending is null)
                    foreach (var range in _recent)
                        pending = pending with { High = Math.Max(pending.High, range.High), Low = Math.Min(pending.Low, range.Low) };
                _pending = pending with { High = Math.Max(pending.High, bar.High), Low = Math.Min(pending.Low, bar.Low),
                    BarCount = _consecutive, IsBullish = metrics.Delta >= 0 };
            }
        }
        // A zero-volume bar leaves the segment intact but still maintains ranges, EMA and zones.
        _observedBars++;
        if (_observedBars > _config.EmaLength + 1)
        {
            var seed = Ema ?? bar.Close;
            Ema = (bar.Close - seed) * (2.0 / (_config.EmaLength + 1.0)) + seed;
        }
        MaintainZones(bar);
        NextBarIndex++;
        return metrics;
    }

    public void Freeze(int endIndex, StopReason reason)
    {
        if (endIndex < 0 || reason is not (StopReason.Session or StopReason.Contract))
            throw new ArgumentOutOfRangeException(nameof(endIndex));
        foreach (var index in _active)
            if (endIndex < _zones[index].CreatedIndex) throw new ArgumentOutOfRangeException(nameof(endIndex));
        foreach (var index in _active)
            _zones[index] = _zones[index] with { EndIndex = endIndex, IsActive = false, StopReason = reason };
        _active.Clear();
    }

    private EffortMetrics Measure(EffortBar bar)
    {
        var delta = bar.AskVolume - bar.BidVolume;
        var seconds = bar.ElapsedMicroseconds / 1_000_000;
        if (bar.TotalVolume == 0) return new(delta, 0, 0, seconds, false, false);
        var percent = Math.Abs(delta) / (double)bar.TotalVolume * 100.0;
        var effort = delta / TickDivisor(bar);
        var matched = delta >= 0 ? bar.Close >= bar.Open : bar.Close < bar.Open;
        var eligible = bar.HasKnownAggressor && bar.HasValidElapsed && matched
            && (_config.MinDeltaPercent == 0 || percent >= _config.MinDeltaPercent)
            && (_config.MaxDeltaPercent == 0 || percent <= _config.MaxDeltaPercent)
            && (_config.MaxDeltaEffort == 0 || Math.Abs(effort) <= _config.MaxDeltaEffort)
            && (_config.MaxTimeSeconds == 0 || seconds <= _config.MaxTimeSeconds);
        return new(delta, percent, effort, seconds, true, eligible);
    }

    private void CreateZone(PendingSegment segment, EffortBar bar)
    {
        var lower = segment.Low;
        var upper = segment.High;
        if (segment.BarCount > _config.FullRangeMaxBars)
        {
            var inset = (upper - lower) / 100.0 * _config.EntryZonePercent;
            if (segment.IsBullish) upper -= inset;
            else lower += inset;
        }
        _active.Add(_zones.Count);
        _zones.Add(new(_nextZoneId++, bar.Index, bar.Index, lower, upper, segment.IsBullish,
            !segment.IsBullish && bar.Close < upper));
    }

    private void MaintainZones(EffortBar bar)
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var index = _active[i];
            var zone = _zones[index];
            var boundary = zone.IsBullish ? zone.Lower : zone.Upper;
            var crossed = zone.StopsOnAbove ? Math.Max(bar.Open, bar.Close) > boundary : Math.Min(bar.Open, bar.Close) < boundary;
            var reason = _config.MaxZoneExtensions != 0 && bar.Index - zone.CreatedIndex >= _config.MaxZoneExtensions
                ? StopReason.Age : crossed ? StopReason.Boundary : StopReason.None;
            if (reason == StopReason.None) continue;
            _zones[index] = zone with { IsActive = false, EndIndex = bar.Index + 1, StopReason = reason };
            _active.RemoveAt(i);
        }
    }

    private static long TickDivisor(EffortBar bar)
    {
        // Decimal safely computes a distance even when signed tick coordinates straddle zero.
        var distance = Math.Abs((decimal)bar.CloseTicks - bar.OpenTicks);
        if (distance >= long.MaxValue) throw new OverflowException("Body tick distance exceeds Int64.");
        return (long)distance + 1;
    }

    private void ValidateBar(EffortBar bar)
    {
        if (bar.Index != NextBarIndex || bar.Index == int.MaxValue)
            throw new ArgumentException("Closed bars must have consecutive, non-repeated indexes.");
        if (!double.IsFinite(bar.Open) || !double.IsFinite(bar.High) || !double.IsFinite(bar.Low) || !double.IsFinite(bar.Close)
            || bar.High < Math.Max(bar.Open, bar.Close) || bar.Low > Math.Min(bar.Open, bar.Close) || bar.Low > bar.High
            || !double.IsFinite(bar.High - bar.Low))
            throw new ArgumentException("Invalid OHLC.");
        if (bar.AskVolume < 0 || bar.BidVolume < 0 || bar.TotalVolume < 0
            || bar.BidVolume > bar.TotalVolume || bar.AskVolume > bar.TotalVolume - bar.BidVolume || bar.ElapsedMicroseconds < 0)
            throw new ArgumentException("Invalid volume or elapsed time.");
        _ = TickDivisor(bar);
    }
}
