namespace WolfMoss.ATAS.PriceMapping.Core;

// Owned by the indicator's data lock. Snapshots contain no mutable state from this book.
public sealed class OptionRollingFlowState
{
    private sealed class ObservationRun(DateTime startUtc)
    {
        public DateTime StartUtc { get; } = startUtc;
        public DateTime? EndUtc { get; set; }
        public List<OptionCumulativeSample> Regular { get; } = new();
        public List<OptionCumulativeSample> All { get; } = new();
        public List<OptionCumulativeSample> Samples(OptionFlowTradeScope scope)
            => scope == OptionFlowTradeScope.RegularTrades ? Regular : All;
    }

    private sealed class ContractSeries(OptionContractDescriptor contract)
    {
        public OptionContractDescriptor Contract { get; } = contract;
        public bool Active { get; set; }
        public (decimal Lower, decimal Upper) Bounds { get; set; }
        public List<ObservationRun> Runs { get; } = new();
    }

    private readonly Dictionary<long, ContractSeries> _series = new();
    private IReadOnlyList<OptionTradingSegment> _segments = Array.Empty<OptionTradingSegment>();
    private OptionTradingSegment? _segment;
    private DateTime _lockedUntilUtc = DateTime.MinValue;
    private DateTime _clockUtc = DateTime.MinValue;
    private decimal _atm;
    private bool _suspended;

    public bool HasLadder => _series.Values.Any(static entry => entry.Active);

    public bool IsLadderLocked(DateTime nowUtc)
        => _segment.HasValue && _segment.Value.Contains(nowUtc) && nowUtc < _lockedUntilUtc;

    public void SetTradingSegments(IReadOnlyList<OptionTradingSegment> segments, DateTime nowUtc)
    {
        _segments = segments.OrderBy(static segment => segment.StartUtc).ToArray();
        AdvanceClock(nowUtc);
    }

    public void AdvanceClock(DateTime nowUtc)
    {
        // A callback and a scheduled refresh can acquire the owning lock in the
        // reverse order of their captured timestamps. Never reopen an old session.
        nowUtc = Max(nowUtc, _clockUtc);
        _clockUtc = nowUtc;
        OptionTradingSegment? next = null;
        foreach (var segment in _segments)
        {
            if (segment.StartUtc > nowUtc)
                break;
            next = segment;
        }

        if (_segment != next)
        {
            _segment = next;
            _lockedUntilUtc = DateTime.MinValue;
            foreach (var entry in _series.Values.ToArray())
            {
                entry.Runs.Clear();
                if (!entry.Active)
                    _series.Remove(entry.Contract.ConId);
                else if (!_suspended && next.HasValue && next.Value.Contains(nowUtc))
                    entry.Runs.Add(new ObservationRun(next.Value.StartUtc));
            }
        }

        if (next.HasValue && nowUtc >= next.Value.EndUtc)
        {
            foreach (var entry in _series.Values)
                CloseRun(entry, next.Value.EndUtc);
        }
    }

    public void RenewAtmLock(decimal atm, DateTime nowUtc, int intervalMinutes)
    {
        _atm = atm;
        _lockedUntilUtc = _segment.HasValue && _segment.Value.Contains(nowUtc)
            ? Min(nowUtc.AddMinutes(OptionFlowAggregation.NormalizeInterval(intervalMinutes)),
                _segment.Value.EndUtc)
            : DateTime.MinValue;
    }

    public void ConfigureLadder(
        IReadOnlyList<OptionContractDescriptor> contracts,
        decimal atm,
        DateTime nowUtc,
        int intervalMinutes)
    {
        AdvanceClock(nowUtc);
        var activeIds = contracts.Select(static contract => contract.ConId).ToHashSet();
        var strikes = contracts.Select(static contract => contract.StrikeUsd)
            .Distinct().OrderBy(static strike => strike).ToArray();
        var bounds = strikes.Select((strike, index) =>
                (Strike: strike, Bounds: OptionStrikeSelection.GetRowBounds(strikes, index)))
            .ToDictionary(static item => item.Strike, static item => item.Bounds);
        foreach (var entry in _series.Values)
        {
            if (entry.Active && !activeIds.Contains(entry.Contract.ConId))
            {
                CloseRun(entry, nowUtc);
                entry.Active = false;
            }
        }

        foreach (var contract in contracts)
        {
            if (!_series.TryGetValue(contract.ConId, out var entry))
            {
                entry = new ContractSeries(contract);
                _series.Add(contract.ConId, entry);
            }
            if (!entry.Active && !_suspended && _segment.HasValue && _segment.Value.Contains(nowUtc))
                entry.Runs.Add(new ObservationRun(nowUtc));
            entry.Active = true;
            entry.Bounds = bounds[contract.StrikeUsd];
        }
        RenewAtmLock(atm, nowUtc, intervalMinutes);
    }

    // Start a new observation run after a connection/subscription gap. The old run
    // remains visible until it ages out; cumulative counters never bridge the gap.
    public void Resume(DateTime nowUtc)
    {
        AdvanceClock(nowUtc);
        _suspended = false;
        nowUtc = Max(nowUtc, _clockUtc);
        if (!_segment.HasValue || !_segment.Value.Contains(nowUtc))
            return;
        foreach (var entry in _series.Values.Where(static entry => entry.Active))
        {
            if (entry.Runs.Count == 0 || entry.Runs[^1].EndUtc.HasValue)
                entry.Runs.Add(new ObservationRun(nowUtc));
        }
    }

    public void Suspend(DateTime nowUtc)
    {
        _suspended = true;
        foreach (var entry in _series.Values)
            CloseRun(entry, nowUtc);
    }

    public bool Add(OptionCumulativeSample sample, OptionFlowTradeScope scope, DateTime receivedUtc)
    {
        if (sample.TotalVolume < 0 || sample.Vwap < 0m || sample.Multiplier <= 0m)
            return false;
        AdvanceClock(receivedUtc);
        if (!_segment.HasValue || !_segment.Value.Contains(sample.SampleUtc)
            || !_series.TryGetValue(sample.ConId, out var entry) || !entry.Active
            || entry.Runs.Count == 0 || sample.SampleUtc > receivedUtc)
            return false;

        var run = entry.Runs[^1];
        if (sample.SampleUtc < run.StartUtc
            || (run.EndUtc.HasValue && sample.SampleUtc >= run.EndUtc.Value))
            return false;

        var samples = run.Samples(scope);
        if (samples.Count > 0)
        {
            var previous = samples[^1];
            // Late or duplicated callbacks must not erase a valid baseline.
            if (sample.SampleUtc <= previous.SampleUtc)
                return false;
            if (sample.TotalVolume < previous.TotalVolume
                || sample.Multiplier != previous.Multiplier
                || sample.CumulativePremium < previous.CumulativePremium)
                samples.Clear();
        }
        samples.Add(sample);
        return true;
    }

    public OptionFlowSnapshot CreateSnapshot(
        string ticker,
        DateOnly expiration,
        DateTime nowUtc,
        int intervalMinutes,
        OptionFlowTradeScope scope,
        int requestedStrikes,
        bool delayed)
    {
        AdvanceClock(nowUtc);
        var minutes = OptionFlowAggregation.NormalizeInterval(intervalMinutes);
        nowUtc = Max(nowUtc, _clockUtc);
        var endUtc = _segment.HasValue ? Min(nowUtc, _segment.Value.EndUtc) : (DateTime?)null;
        var startUtc = endUtc.HasValue
            ? Max(endUtc.Value.AddMinutes(-minutes), _segment!.Value.StartUtc)
            : (DateTime?)null;
        var results = new Dictionary<long, (OptionIntervalValue? Value, OptionFlowCoverage Coverage)>();

        if (startUtc.HasValue && endUtc.HasValue)
        {
            foreach (var entry in _series.Values.ToArray())
            {
                Trim(entry, startUtc.Value);
                results[entry.Contract.ConId] = Calculate(entry, scope, startUtc.Value, endUtc.Value,
                    endUtc - startUtc < TimeSpan.FromMinutes(minutes));
                if (!entry.Active && !entry.Runs.Any(run =>
                        run.Regular.Any(sample => sample.SampleUtc >= startUtc.Value)
                        || run.All.Any(sample => sample.SampleUtc >= startUtc.Value)))
                {
                    _series.Remove(entry.Contract.ConId);
                    results.Remove(entry.Contract.ConId);
                }
            }
        }

        var visible = _series.Values.Where(entry => entry.Active
            || (results.TryGetValue(entry.Contract.ConId, out var result) && result.Value.HasValue))
            .GroupBy(static entry => entry.Contract.StrikeUsd)
            .OrderBy(static group => group.Key);
        var rows = new List<OptionStrikeRow>();
        foreach (var group in visible)
        {
            var call = group.FirstOrDefault(static entry => entry.Contract.Right == OptionRight.Call);
            var put = group.FirstOrDefault(static entry => entry.Contract.Right == OptionRight.Put);
            var c = GetResult(call, results);
            var p = GetResult(put, results);
            var bounds = group.First().Bounds;
            rows.Add(new OptionStrikeRow(group.Key, null, null,
                c.Value?.Premium, p.Value?.Premium, c.Value?.Volume, p.Value?.Volume,
                c.Value?.ObservedStartUtc, p.Value?.ObservedStartUtc,
                c.Value?.IsPartial ?? false, p.Value?.IsPartial ?? false,
                c.Coverage, p.Coverage,
                call != null && !call.Active, put != null && !put.Active,
                bounds.Lower, bounds.Upper));
        }

        var activeStrikes = _series.Values.Where(static entry => entry.Active)
            .Select(static entry => entry.Contract.StrikeUsd).ToHashSet();
        var retainedCount = rows.Count(row => !activeStrikes.Contains(row.StrikeUsd));
        var valueCount = rows.Sum(static row => (row.CallPremium.HasValue ? 1 : 0)
            + (row.PutPremium.HasValue ? 1 : 0));
        var partialCount = rows.Sum(static row => (row.CallFlowIsPartial ? 1 : 0)
            + (row.PutFlowIsPartial ? 1 : 0));
        var isOpen = _segment.HasValue && _segment.Value.Contains(nowUtc);
        var waitingForCoverage = startUtc.HasValue && _series.Values
            .Where(static entry => entry.Active)
            .All(entry => entry.Runs.Count == 0 || entry.Runs[0].StartUtc > startUtc.Value);
        var status = delayed ? OptionDataStatus.Delayed
            : !isOpen ? OptionDataStatus.Closed
            : valueCount == 0 && (waitingForCoverage
                || endUtc - startUtc < TimeSpan.FromMinutes(minutes))
                ? OptionDataStatus.Warming
            : partialCount > 0 ? OptionDataStatus.Partial : OptionDataStatus.Live;
        var message = $"{minutes}m 滚动；数据 {valueCount}/{rows.Count * 2}"
            + (partialCount > 0 ? $"；部分 {partialCount}" : string.Empty)
            + (retainedCount > 0 ? $"；保留 {retainedCount} 档" : string.Empty);
        return new OptionFlowSnapshot(ticker, expiration, _atm, rows.ToArray(), status,
            startUtc, endUtc, nowUtc, message, OptionFlowBucketMode.Rolling, scope, minutes,
            requestedStrikes, activeStrikes.Count,
            _lockedUntilUtc > nowUtc ? _lockedUntilUtc : null, retainedCount);
    }

    public void Clear()
    {
        _series.Clear();
        _segments = Array.Empty<OptionTradingSegment>();
        _segment = null;
        _lockedUntilUtc = DateTime.MinValue;
        _clockUtc = DateTime.MinValue;
        _atm = 0m;
        _suspended = false;
    }

    private static (OptionIntervalValue? Value, OptionFlowCoverage Coverage) GetResult(
        ContractSeries? entry,
        IReadOnlyDictionary<long, (OptionIntervalValue? Value, OptionFlowCoverage Coverage)> results)
        => entry == null ? (null, OptionFlowCoverage.NoData)
            : results.TryGetValue(entry.Contract.ConId, out var result) ? result
            : (null, OptionFlowCoverage.NoEvents);

    private static (OptionIntervalValue? Value, OptionFlowCoverage Coverage) Calculate(
        ContractSeries entry, OptionFlowTradeScope scope, DateTime startUtc, DateTime endUtc,
        bool shortWindow)
    {
        OptionIntervalValue? total = null;
        var hasEvents = false;
        var invalidRun = false;
        foreach (var run in entry.Runs)
        {
            var samples = run.Samples(scope);
            var runEnd = run.EndUtc.HasValue ? Min(endUtc, run.EndUtc.Value) : endUtc;
            var inside = samples.Where(sample => sample.SampleUtc >= startUtc
                && sample.SampleUtc < runEnd).ToArray();
            if (inside.Length == 0)
                continue;
            hasEvents = true;
            var calculated = OptionFlowAggregation.TryCalculateBucketValue(
                samples, startUtc, runEnd, allowPartial: true, out var value);
            if (!calculated && !samples.Any(sample => sample.SampleUtc < startUtc)
                && inside.Length >= 2
                && OptionFlowAggregation.TryCalculateDelta(inside[0], inside[^1], out value))
            {
                value = value with { IsPartial = true };
                calculated = true;
            }
            if (!calculated)
            {
                invalidRun = true;
                continue;
            }
            value = value with { IsPartial = value.IsPartial || shortWindow
                || run.StartUtc > startUtc || runEnd < endUtc };
            total = total.HasValue
                ? new OptionIntervalValue(checked(total.Value.Volume + value.Volume),
                    total.Value.Premium + value.Premium,
                    Min(total.Value.ObservedStartUtc!.Value, value.ObservedStartUtc!.Value), true)
                : value;
        }
        if (total.HasValue)
        {
            total = total.Value with { IsPartial = total.Value.IsPartial || invalidRun };
            return (total, total.Value.IsPartial ? OptionFlowCoverage.Partial : OptionFlowCoverage.Full);
        }
        return (null, hasEvents ? OptionFlowCoverage.NoData : OptionFlowCoverage.NoEvents);
    }

    private static void Trim(ContractSeries entry, DateTime startUtc)
    {
        entry.Runs.RemoveAll(run => run.EndUtc.HasValue && run.EndUtc.Value <= startUtc);
        foreach (var run in entry.Runs)
        {
            TrimSamples(run.Regular, startUtc);
            TrimSamples(run.All, startUtc);
        }
    }

    private static void TrimSamples(List<OptionCumulativeSample> samples, DateTime startUtc)
    {
        var index = 0;
        while (index + 1 < samples.Count && samples[index + 1].SampleUtc < startUtc)
            index++;
        if (index > 0)
            samples.RemoveRange(0, index);
    }

    private static void CloseRun(ContractSeries entry, DateTime nowUtc)
    {
        if (entry.Runs.Count > 0 && !entry.Runs[^1].EndUtc.HasValue)
            entry.Runs[^1].EndUtc = nowUtc;
    }

    private static DateTime Min(DateTime first, DateTime second) => first < second ? first : second;
    private static DateTime Max(DateTime first, DateTime second) => first > second ? first : second;
}
