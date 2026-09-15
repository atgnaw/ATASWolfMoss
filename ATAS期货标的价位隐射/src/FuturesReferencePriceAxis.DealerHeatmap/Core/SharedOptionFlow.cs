namespace WolfMoss.ATAS.PriceMapping.Core;

internal readonly record struct OptionFlowGroupKey(string Connection, string Ticker, DateOnly Expiration,
    OptionFlowBucketMode Mode, int Minutes, OptionFlowTradeScope Scope, int Levels);

// One book per settings group, never one book per chart. The registry stores no
// ATAS objects; releasing the last lease releases all buffered observations.
internal static class SharedOptionFlow
{
    private static readonly object Sync = new();
    private static readonly Dictionary<OptionFlowGroupKey, Entry> Entries = new();
    private sealed class Entry(OptionFlowGroupKey key, DateTime now)
    { public readonly OptionFlowGroup Book = new(key, now); public int References; }
    public static Lease Acquire(OptionFlowGroupKey key, DateTime now)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out var entry)) Entries.Add(key, entry = new(key, now));
            entry.References++;
            return new(key, entry.Book);
        }
    }
    internal sealed class Lease(OptionFlowGroupKey key, OptionFlowGroup book) : IDisposable
    {
        public OptionFlowGroupKey Key { get; } = key;
        public OptionFlowGroup Book { get; } = book;
        private bool _observing;
        private int _disposed;
        public void Observe(bool observing, DateTime now)
        {
            lock (Sync)
            {
                if (_disposed != 0 || _observing == observing) return;
                _observing = observing;
                Book.ObservationChanged(observing ? 1 : -1, now);
            }
        }
        public void Dispose()
        {
            lock (Sync)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_observing) Book.ObservationChanged(-1, DateTime.UtcNow);
                if (Entries.TryGetValue(Key, out var entry) && --entry.References == 0) Entries.Remove(Key);
            }
        }
    }
}

internal sealed partial class OptionFlowGroup
{
    private sealed class Run(DateTime start)
    { public DateTime Start = start; public DateTime? End; public readonly OptionSampleBuffer Samples = new(); }
    private sealed class Series(OptionContractDescriptor contract)
    { public readonly OptionContractDescriptor Contract = contract; public bool Active; public DateTime? LastDeferredSource; public readonly List<Run> Runs = new(); }
    private sealed record FixedLadder(decimal Atm, OptionContractDescriptor[] Contracts);
    private readonly object _sync = new();
    private readonly OptionFlowGroupKey _key;
    private readonly OptionRollingFlowState _rolling = new();
    private readonly Dictionary<long, Series> _series = new();
    private readonly Dictionary<DateTime, FixedLadder> _fixedLadders = new();
    private OptionContractDescriptor[] _contracts = [];
    private IReadOnlyList<OptionTradingSegment> _segments = Array.Empty<OptionTradingSegment>();
    private decimal _atm;
    private long _rangeRevision = -1;
    private int _observers;
    private DateTime _lastNow, _created, _lastPublished;
    private OptionFlowSnapshot? _snapshot;
    private readonly RealtimeClockGuard _clock = new();
    private DateTime _lastPruned;
    private DateTime? _receiverBoundary;
    private IReadOnlyList<OptionContractDescriptor>? _configuredInput;
    private bool _rangeDirty;
    internal long AcceptedSamples { get; private set; }
    public OptionFlowGroup(OptionFlowGroupKey key, DateTime now) { _key = key; _created = _lastNow = now; }
    public long CachedSamples
    { get { lock (_sync) return _rolling.CachedSampleCount + _series.Values.Sum(s => s.Runs.Sum(r => (long)r.Samples.Count)); } }

    public void Configure(IReadOnlyList<OptionContractDescriptor> contracts, decimal atm,
        IReadOnlyList<OptionTradingSegment> segments, long revision, DateTime now)
    {
        lock (_sync)
        {
            if (revision < _rangeRevision) return;
            DrainFuture(now);
            Advance(now);
            if (!_rangeDirty && ReferenceEquals(_configuredInput, contracts) && ReferenceEquals(_segments, segments) && _atm == atm)
            { _rangeRevision = revision; RememberLadder(now); return; }
            var segmentsChanged = !ReferenceEquals(_segments, segments);
            if (segmentsChanged && !_segments.SequenceEqual(segments)) DropAllFuture();
            _segments = segments;
            if (!_rangeDirty && SameContracts(contracts))
            {
                if (segmentsChanged) _rolling.SetTradingSegments(segments, now);
                _atm = atm; _configuredInput = contracts; _rangeRevision = revision; RememberLadder(now); return;
            }
            var ids = contracts.Select(c => c.ConId).ToHashSet();
            foreach (var old in _series.Values)
                if (old.Active && !ids.Contains(old.Contract.ConId)) Retire(old.Contract.ConId, now);
            _contracts = contracts.ToArray(); _atm = atm; _rangeRevision = revision;
            _configuredInput = contracts; _rangeDirty = false;
            foreach (var contract in contracts)
            {
                if (!_series.TryGetValue(contract.ConId, out var series)) _series.Add(contract.ConId, series = new(contract));
                if (!series.Active && _observers > 0) series.Runs.Add(new(now));
                series.Active = true;
            }
            _rolling.SetTradingSegments(segments, now);
            _rolling.ConfigureLadder(contracts, atm, now, _key.Minutes);
            if (_observers == 0) _rolling.Suspend(now);
            RememberLadder(now);
        }
    }
    public void ObservationChanged(int delta, DateTime now)
    {
        lock (_sync)
        {
            var was = _observers;
            DrainFuture(now);
            _observers = Math.Max(0, _observers + delta);
            if (was > 0 && _observers == 0)
            { DropAllFuture(); foreach (var series in _series.Values) Close(series, now); _rolling.Suspend(now); }
            if (was == 0 && _observers > 0)
            { foreach (var series in _series.Values.Where(s => s.Active)) series.Runs.Add(new(now)); _rolling.Resume(now); }
        }
    }
    public void Retire(long conId, DateTime now)
    {
        lock (_sync)
        {
            DrainFuture(now);
            RetireCore(conId, now);
        }
    }
    private void RetireCore(long conId, DateTime now)
    {
        DropFuture(conId);
        _rangeDirty = true;
        if (_series.TryGetValue(conId, out var series)) { Close(series, now); series.Active = false; }
        _rolling.RetireContract(conId, now);
    }
    public bool Receive(OptionCumulativeSample sample, DateTime received)
        => ReceiveDetailed(sample, received) == FlowSampleDisposition.Accepted;

    private FlowSampleDisposition ApplyReceived(OptionCumulativeSample sample, DateTime received)
    {
        lock (_sync)
        {
            if (!sample.HasValidCumulativeValue()) return FlowSampleDisposition.Invalid;
            if (sample.SampleUtc > received) return FlowSampleDisposition.FutureSource;
            if (_observers == 0) return FlowSampleDisposition.ObservationGap;
            if (sample.SampleUtc < _created) return FlowSampleDisposition.BeforeCoverage;
            if (!_series.TryGetValue(sample.ConId, out var series) || !series.Active) return FlowSampleDisposition.OutsideLadder;
            OptionTradingSegment segment = default;
            for (var i = 0; i < _segments.Count; i++)
                if (_segments[i].Contains(sample.SampleUtc)) { segment = _segments[i]; break; }
            if (segment == default) return FlowSampleDisposition.OutsideSession;
            if (_key.Mode == OptionFlowBucketMode.Rolling)
            { var result = _rolling.AddDetailed(sample, _key.Scope, received); if (result == FlowSampleDisposition.Accepted) AcceptedSamples++; return result; }
            if (series.Runs.Count == 0) series.Runs.Add(new(received));
            var run = series.Runs[^1];
            if (run.End.HasValue) return FlowSampleDisposition.ObservationGap;
            if (sample.SampleUtc < run.Start) return FlowSampleDisposition.BeforeCoverage;
            if (run.Samples.Count > 0)
            {
                var last = run.Samples[^1];
                // Same event delivered to multiple chart callbacks is applied once.
                if (sample.SampleUtc == last.SampleUtc) return FlowSampleDisposition.Duplicate;
                if (sample.SampleUtc < last.SampleUtc) return FlowSampleDisposition.OutOfOrder;
                if (!segment.Contains(last.SampleUtc) || sample.TotalVolume < last.TotalVolume
                    || sample.CumulativePremium < last.CumulativePremium || sample.Multiplier != last.Multiplier)
                { Close(series, sample.SampleUtc); series.Runs.Add(run = new(sample.SampleUtc)); }
            }
            if (run.Samples.Count >= 65536) run.Samples.RemovePrefix(32768);
            run.Samples.Add(sample); AcceptedSamples++; return FlowSampleDisposition.Accepted;
        }
    }
    public OptionFlowSnapshot Read(DateTime now, bool delayed)
    {
        lock (_sync)
        {
            DrainFuture(now);
            Advance(now); RememberLadder(now);
            if (_snapshot != null && _lastPublished.Ticks / TimeSpan.TicksPerSecond == now.Ticks / TimeSpan.TicksPerSecond)
                return _snapshot;
            var open = false;
            for (var i = 0; i < _segments.Count; i++) if (_segments[i].Contains(now)) { open = true; break; }
            _lastPublished = now;
            if (_observers == 0 && _snapshot != null)
                return _snapshot = _snapshot with { Status = open ? OptionDataStatus.Frozen : OptionDataStatus.Closed };
            if (_key.Mode == OptionFlowBucketMode.Rolling)
                return _snapshot = _rolling.CreateSnapshot(_key.Ticker, _key.Expiration, now, _key.Minutes, _key.Scope, _key.Levels, delayed)
                    with { AtmLockedUntilUtc = _receiverBoundary > now ? _receiverBoundary : null };
            OptionTradingSegment segment = default;
            for (var i = 0; i < _segments.Count; i++)
                if (_segments[i].Contains(now) || _segments[i].EndUtc <= now) segment = _segments[i];
            var bucket = segment == default ? null : OptionFlowAggregation.GetPreviousCompletedBucket(now, segment, _key.Minutes, TimeSpan.FromSeconds(3));
            if (!bucket.HasValue || !_fixedLadders.TryGetValue(bucket.Value.StartUtc, out var ladder))
                return _snapshot = OptionFlowSnapshot.Waiting(now, "等待共享组的第一个已完成桶", _key.Mode, _key.Scope, _key.Minutes)
                    with { Ticker = _key.Ticker, Expiration = _key.Expiration, Status = open ? OptionDataStatus.Warming : OptionDataStatus.Closed };
            var state = delayed ? OptionDataStatus.Delayed : open ? OptionDataStatus.Live : OptionDataStatus.Closed;
            if (_snapshot?.BucketEndUtc == bucket.Value.EndUtc)
                return _snapshot = _snapshot.Status == state ? _snapshot : _snapshot with { Status = state };
            var rows = new List<OptionStrikeRow>();
            foreach (var strike in ladder.Contracts.Select(c => c.StrikeUsd).Distinct().Order())
            {
                var call = ladder.Contracts.FirstOrDefault(c => c.StrikeUsd == strike && c.Right == OptionRight.Call);
                var put = ladder.Contracts.FirstOrDefault(c => c.StrikeUsd == strike && c.Right == OptionRight.Put);
                var c = Value(call.ConId, bucket.Value.StartUtc, bucket.Value.EndUtc);
                var p = Value(put.ConId, bucket.Value.StartUtc, bucket.Value.EndUtc);
                rows.Add(new(strike, null, null, c.Value?.Premium, p.Value?.Premium, c.Value?.Volume, p.Value?.Volume,
                    c.Value?.ObservedStartUtc, p.Value?.ObservedStartUtc, c.Value?.IsPartial ?? false, p.Value?.IsPartial ?? false, c.Coverage, p.Coverage));
            }
            var count = rows.Sum(r => (r.CallPremium.HasValue ? 1 : 0) + (r.PutPremium.HasValue ? 1 : 0));
            return _snapshot = new(_key.Ticker, _key.Expiration, ladder.Atm, rows.ToArray(), state,
                bucket.Value.StartUtc, bucket.Value.EndUtc, now, $"{_key.Minutes}m 共享已完成桶；数据 {count}/{rows.Count * 2}",
                _key.Mode, _key.Scope, _key.Minutes, _key.Levels, rows.Count);
        }
    }
    private (OptionIntervalValue? Value, OptionFlowCoverage Coverage) Value(long conId, DateTime start, DateTime end)
    {
        if (!_series.TryGetValue(conId, out var series)) return (null, OptionFlowCoverage.NoData);
        OptionIntervalValue? total = null; var hadEvents = false; var invalid = false;
        foreach (var run in series.Runs)
        {
            var stop = run.End.HasValue && run.End.Value < end ? run.End.Value : end;
            var first = OptionFlowAggregation.LowerBound(run.Samples, start);
            var last = OptionFlowAggregation.LowerBound(run.Samples, stop) - 1;
            if (first > last) continue;
            hadEvents = true;
            var ok = OptionFlowAggregation.TryCalculateBucketValue(run.Samples, start, stop, true, out var value, run.Start);
            if (!ok && last > first && OptionFlowAggregation.TryCalculateDelta(run.Samples[first], run.Samples[last], out value))
            { ok = true; value = value with { IsPartial = true }; }
            if (!ok) { invalid = true; continue; }
            value = value with { IsPartial = value.IsPartial || run.Start > start || stop < end };
            try { total = total.HasValue ? new(checked(total.Value.Volume + value.Volume), total.Value.Premium + value.Premium,
                total.Value.ObservedStartUtc < value.ObservedStartUtc ? total.Value.ObservedStartUtc : value.ObservedStartUtc, true) : value; }
            catch (OverflowException) { return (null, OptionFlowCoverage.NoData); }
        }
        if (total.HasValue) { total = total.Value with { IsPartial = total.Value.IsPartial || invalid }; return (total, total.Value.IsPartial ? OptionFlowCoverage.Partial : OptionFlowCoverage.Full); }
        return (null, hadEvents ? OptionFlowCoverage.NoData : OptionFlowCoverage.NoEvents);
    }
    private void RememberLadder(DateTime now)
    {
        if (_key.Mode != OptionFlowBucketMode.PreviousCompletedFixed || _contracts.Length == 0) return;
        OptionTradingSegment segment = default;
        for (var i = 0; i < _segments.Count; i++)
            if (_segments[i].Contains(now)) { segment = _segments[i]; break; }
        if (segment == default) return;
        var start = OptionFlowAggregation.GetFixedBucketStart(now, segment, _key.Minutes);
        if (!_fixedLadders.ContainsKey(start)) _fixedLadders.Add(start, new(_atm, _contracts));
    }
    private bool SameContracts(IReadOnlyList<OptionContractDescriptor> contracts)
    {
        if (_contracts.Length != contracts.Count) return false;
        for (var i = 0; i < contracts.Count; i++) if (_contracts[i] != contracts[i]) return false;
        return true;
    }
    public void ObserveClock(DateTime now, long timestamp, long frequency)
    {
        lock (_sync)
        {
            if (!_clock.Observe(now, timestamp, frequency)) return;
            DropAllFuture();
            _series.Clear(); _contracts = []; _rolling.Clear(); _fixedLadders.Clear();
            _snapshot = null; _created = _lastNow = now; _rangeRevision = -1;
            _configuredInput = null; _rangeDirty = true;
            _lastPruned = _lastPublished = DateTime.MinValue;
        }
    }
    public void SetReceiverBoundary(long revision, DateTime next)
    { lock (_sync) if (revision >= _rangeRevision) _receiverBoundary = next; }
    private void Advance(DateTime now)
    {
        // Captured callback/publication times may acquire the lock out of order.
        // Only the monotonic clock guard may reset observations, not this ordering.
        if (now < _lastNow) now = _lastNow;
        _lastNow = now;
        if (now - _lastPruned < TimeSpan.FromSeconds(1)) return;
        _lastPruned = now;
        PruneBefore(now.AddMinutes(-(_key.Minutes * 2 + 1)));
    }
    private void PruneBefore(DateTime cutoff)
    {
        foreach (var key in _fixedLadders.Keys.Where(k => k < cutoff).ToArray()) _fixedLadders.Remove(key);
        foreach (var series in _series.Values.ToArray())
        {
            series.Runs.RemoveAll(r => r.End.HasValue && r.End.Value < cutoff);
            foreach (var run in series.Runs)
                run.Samples.RemovePrefix(Math.Max(0, OptionFlowAggregation.LowerBound(run.Samples, cutoff) - 1));
            if (!series.Active && series.Runs.Count == 0) _series.Remove(series.Contract.ConId);
        }
    }
    private static void Close(Series series, DateTime now)
    { if (series.Runs.Count > 0 && !series.Runs[^1].End.HasValue) series.Runs[^1].End = now; }
}
