namespace WolfMoss.ATAS.PriceMapping.Core;

public static class OptionFlowAggregation
{
    public static bool TryCalculateDelta(
        OptionCumulativeSample start,
        OptionCumulativeSample end,
        out OptionIntervalValue value)
    {
        value = OptionIntervalValue.Zero;

        if (!start.HasValidCumulativeValue() || !end.HasValidCumulativeValue()
            || start.ConId != end.ConId
            || end.SampleUtc < start.SampleUtc
            || start.TotalVolume < 0
            || end.TotalVolume < start.TotalVolume
            || start.Vwap < 0m
            || end.Vwap < 0m
            || start.Multiplier <= 0m
            || end.Multiplier != start.Multiplier)
        {
            return false;
        }

        var volume = end.TotalVolume - start.TotalVolume;
        var premium = end.CumulativePremium - start.CumulativePremium;

        if (premium < 0m)
            return false;

        value = new OptionIntervalValue(
            volume,
            premium,
            start.SampleUtc,
            false);
        return true;
    }

    public static bool TryCalculateBucketValue(
        IReadOnlyList<OptionCumulativeSample> samples,
        DateTime bucketStartUtc,
        DateTime bucketEndUtc,
        bool allowPartial,
        out OptionIntervalValue value,
        DateTime? earliestBaselineUtc = null)
    {
        value = OptionIntervalValue.Zero;

        if (bucketEndUtc <= bucketStartUtc || samples.Count == 0)
            return false;

        // Ingestion maintains source-time order. Find [start, end) without walking
        // the entire retained history; keep the strictly-before-start baseline.
        var first = LowerBound(samples, bucketStartUtc);
        var last = LowerBound(samples, bucketEndUtc) - 1;
        OptionCumulativeSample? baseline = first > 0 ? samples[first - 1] : null;
        if (baseline.HasValue && earliestBaselineUtc.HasValue && baseline.Value.SampleUtc < earliestBaselineUtc.Value)
            baseline = null;
        OptionCumulativeSample? firstInside = first <= last ? samples[first] : null;
        OptionCumulativeSample? end = first <= last ? samples[last] : null;

        // No event was observed inside this bucket. Unknown is intentionally
        // different from a confirmed zero-volume interval.
        if (!firstInside.HasValue || !end.HasValue)
            return false;

        if (baseline.HasValue)
        {
            if (!TryCalculateDelta(baseline.Value, end.Value, out var complete))
                return false;

            value = complete with
            {
                ObservedStartUtc = bucketStartUtc,
                IsPartial = false
            };
            return true;
        }

        if (!allowPartial
            || !firstInside.Value.TryGetLastTradeValue(out var firstTrade))
        {
            return false;
        }

        if (!TryCalculateDelta(firstInside.Value, end.Value, out var remainder))
            return false;

        try
        {
            value = new OptionIntervalValue(checked(firstTrade.Volume + remainder.Volume),
                firstTrade.Premium + remainder.Premium, firstInside.Value.SampleUtc, true);
        }
        catch (OverflowException) { return false; }
        return true;
    }

    public static bool ShouldHoldFixedLadder(
        DateTime activeBucketStartUtc,
        DateTime currentBucketStartUtc,
        DateTime utcNow,
        TimeSpan publicationGrace)
        => activeBucketStartUtc != DateTime.MinValue
           && (currentBucketStartUtc <= activeBucketStartUtc
               || utcNow < currentBucketStartUtc + publicationGrace);

    public static int LowerBound(IReadOnlyList<OptionCumulativeSample> samples, DateTime utc)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (samples[middle].SampleUtc < utc) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public static DateTime GetFixedBucketStart(
        DateTime utcTime,
        OptionTradingSegment segment,
        int intervalMinutes)
    {
        if (!segment.Contains(utcTime))
            throw new ArgumentOutOfRangeException(nameof(utcTime));

        var interval = TimeSpan.FromMinutes(NormalizeInterval(intervalMinutes));
        var bucketIndex = (long)((utcTime - segment.StartUtc).Ticks / interval.Ticks);
        return segment.StartUtc.AddTicks(bucketIndex * interval.Ticks);
    }

    public static (DateTime StartUtc, DateTime EndUtc)? GetPreviousCompletedBucket(
        DateTime utcNow,
        OptionTradingSegment segment,
        int intervalMinutes,
        TimeSpan publicationGrace)
    {
        var interval = TimeSpan.FromMinutes(NormalizeInterval(intervalMinutes));
        var effectiveNow = utcNow - publicationGrace;

        if (effectiveNow < segment.StartUtc + interval)
            return null;

        var capped = effectiveNow < segment.EndUtc ? effectiveNow : segment.EndUtc;
        var completedCount = (long)((capped - segment.StartUtc).Ticks / interval.Ticks);

        if (completedCount <= 0)
            return null;

        var end = segment.StartUtc.AddTicks(completedCount * interval.Ticks);

        if (end > segment.EndUtc)
            return null;

        return (end - interval, end);
    }

    public static int NormalizeInterval(int value)
        => value switch
        {
            1 => 1,
            3 => 3,
            10 => 10,
            _ => 5
        };
}

public sealed class OptionRollingWindow
{
    private readonly Dictionary<long, List<OptionCumulativeSample>> _samples = new();

    public void Add(OptionCumulativeSample sample, TimeSpan window)
    {
        if (!_samples.TryGetValue(sample.ConId, out var samples))
        {
            samples = new List<OptionCumulativeSample>();
            _samples.Add(sample.ConId, samples);
        }

        if (samples.Count > 0
            && (sample.SampleUtc < samples[^1].SampleUtc
                || sample.TotalVolume < samples[^1].TotalVolume))
        {
            samples.Clear();
        }

        if (samples.Count > 0 && samples[^1].SampleUtc == sample.SampleUtc)
            samples[^1] = sample;
        else
            samples.Add(sample);

        var cutoff = sample.SampleUtc - window - TimeSpan.FromMinutes(1);
        var removeCount = 0;

        while (removeCount + 1 < samples.Count
               && samples[removeCount + 1].SampleUtc <= cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
            samples.RemoveRange(0, removeCount);
    }

    public bool TryGetValue(
        long conId,
        DateTime utcNow,
        TimeSpan window,
        out OptionIntervalValue value)
    {
        value = OptionIntervalValue.Zero;

        if (!_samples.TryGetValue(conId, out var samples) || samples.Count < 2)
            return false;

        var cutoff = utcNow - window;
        OptionCumulativeSample? start = null;

        foreach (var sample in samples)
        {
            if (sample.SampleUtc > cutoff)
                break;

            start = sample;
        }

        var end = samples[^1];

        if (!start.HasValue || end.SampleUtc - start.Value.SampleUtc < window)
            return false;

        return OptionFlowAggregation.TryCalculateDelta(start.Value, end, out value);
    }

    public void Clear() => _samples.Clear();
}
