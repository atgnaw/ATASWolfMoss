namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Diagnostics;

public enum PerformanceDiagnosticsMode { Off, Summary, Record }
public enum PerformanceMetric { Render, Publish, Callback }

public sealed record DurationSummary(long Count, double MeanMs, double P95UpperMs);

// Atomic per-second histogram: bounded memory, no application locks on producers.
// P95 is an upper bound (powers of two microseconds), not an exact quantile.
public sealed class PerformanceDurationWindow
{
    private sealed class Bucket(long second)
    {
        public readonly long Second = second;
        public readonly long[] Histogram = new long[32];
        public long Ticks;
    }
    private readonly Bucket?[] _buckets = new Bucket?[60];

    public void Record(long elapsedTicks, DateTime utc)
    {
        if (elapsedTicks < 0) return;
        var second = utc.Ticks / TimeSpan.TicksPerSecond;
        var index = (int)(second % _buckets.Length);
        var bucket = Volatile.Read(ref _buckets[index]);
        if (bucket?.Second != second)
        {
            if (bucket != null && bucket.Second > second) return;
            var replacement = new Bucket(second);
            var winner = Interlocked.CompareExchange(ref _buckets[index], replacement, bucket);
            bucket = winner == bucket ? replacement : winner;
            if (bucket?.Second != second) return;
        }
        var micros = elapsedTicks * (1_000_000d / Stopwatch.Frequency);
        var bin = micros <= 1 ? 0 : Math.Min(31, (int)Math.Ceiling(Math.Log2(micros)));
        Interlocked.Add(ref bucket.Ticks, elapsedTicks);
        Interlocked.Increment(ref bucket.Histogram[bin]);
    }

    public DurationSummary Read(DateTime utc)
    {
        var second = utc.Ticks / TimeSpan.TicksPerSecond;
        var histogram = new long[32];
        long ticks = 0, count = 0;
        foreach (var slot in _buckets)
        {
            var bucket = slot;
            if (bucket == null || bucket.Second > second || bucket.Second <= second - 60) continue;
            ticks += Interlocked.Read(ref bucket.Ticks);
            for (var i = 0; i < histogram.Length; i++)
                histogram[i] += Interlocked.Read(ref bucket.Histogram[i]);
        }
        foreach (var value in histogram) count += value;
        if (count == 0) return new(0, 0, 0);
        long cumulative = 0;
        var percentile = 0d;
        for (var i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= Math.Ceiling(count * .95)) { percentile = Math.Pow(2, i) / 1000; break; }
        }
        return new(count, ticks * 1000d / Stopwatch.Frequency / count, percentile);
    }
}

public sealed record PerformanceSnapshot(DateTime Utc, DurationSummary Render,
    DurationSummary Publish, DurationSummary Callback, double EventsPerSecond,
    double SendsPerSecond, double CancelsPerSecond, int Lines, int Budget, int Consumers,
    long CachedSamples, long OiValues, long GatewayReplacements, int BackgroundTasks,
    long DiagnosticSamplesDropped, string RecorderState, string LastErrorCode = "NONE")
{
    // Batch 1 has synchronous IB callbacks, not an independently observable application queue.
    public int? EventQueueLength => null;
    public long? MarketDataGaps => null;
}

public sealed class PerformanceCollector
{
    private readonly PerformanceDurationWindow[] _durations =
        [new(), new(), new()];
    private int _enabled;
    private long _events;
    private long _cachedSamples;
    private long _oiValues;
    private int _backgroundTasks;
    private long _previousEvents;
    private long _previousSends;
    private long _previousCancels;
    private long _gatewayId;
    private long _replacements;
    private DateTime? _previousUtc;
    private string _lastError = "NONE";
    public void RecordError(string code)
    {
        if (!Enabled) return;
        Volatile.Write(ref _lastError, code switch
        {
            "LINE_LIMIT" or "NO_PERMISSION" or "NO_CONTRACTS" or "PACING" or "CONNECT_TIMEOUT"
                or "CONNECTION_CLOSED" or "INTERNAL" => code,
            _ => "OTHER"
        });
    }
    public bool Enabled { get => Volatile.Read(ref _enabled) != 0; set => Volatile.Write(ref _enabled, value ? 1 : 0); }
    public void EventReceived() { if (Enabled) Interlocked.Increment(ref _events); }
    public void SetCacheCounts(long samples, long oi)
    { Interlocked.Exchange(ref _cachedSamples, samples); Interlocked.Exchange(ref _oiValues, oi); }
    public IDisposable TrackTask() => new TaskLease(this);
    public Measurement Measure(PerformanceMetric metric) => Enabled ? new(this, metric) : default;
    public readonly struct Measurement : IDisposable
    {
        private readonly PerformanceCollector? _owner;
        private readonly PerformanceMetric _metric;
        private readonly long _start;
        internal Measurement(PerformanceCollector owner, PerformanceMetric metric)
        { _owner = owner; _metric = metric; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_owner?.Enabled == true)
                _owner._durations[(int)_metric].Record(Stopwatch.GetTimestamp() - _start, DateTime.UtcNow);
        }
    }
    private sealed class TaskLease : IDisposable
    {
        private PerformanceCollector? _owner;
        public TaskLease(PerformanceCollector owner) { _owner = owner; Interlocked.Increment(ref owner._backgroundTasks); }
        public void Dispose()
        { var owner = Interlocked.Exchange(ref _owner, null); if (owner != null) Interlocked.Decrement(ref owner._backgroundTasks); }
    }
    // One diagnostics sampler owns rate baselines. Never called by OnRender.
    public PerformanceSnapshot Sample(DateTime utc, IbPerformanceSnapshot? gateway, int budget,
        long dropped = 0, string recorderState = "OFF")
    {
        var events = Interlocked.Read(ref _events);
        var seconds = _previousUtc.HasValue ? Math.Max(.001, (utc - _previousUtc.Value).TotalSeconds) : 1;
        var sameGateway = gateway != null && gateway.InstanceId == _gatewayId;
        var sendRate = sameGateway ? Math.Max(0, gateway!.SendAttempts - _previousSends) / seconds : 0;
        var cancelRate = sameGateway ? Math.Max(0, gateway!.CancelAttempts - _previousCancels) / seconds : 0;
        if (gateway != null && gateway.InstanceId != _gatewayId)
        {
            if (_gatewayId != 0) _replacements++;
            _gatewayId = gateway.InstanceId;
        }
        _previousSends = gateway?.SendAttempts ?? 0;
        _previousCancels = gateway?.CancelAttempts ?? 0;
        var eventRate = _previousUtc.HasValue ? Math.Max(0, events - _previousEvents) / seconds : 0;
        _previousEvents = events;
        _previousUtc = utc;
        return new(utc, _durations[0].Read(utc), _durations[1].Read(utc), _durations[2].Read(utc),
            eventRate, sendRate, cancelRate, gateway?.ActiveLines ?? 0, budget, gateway?.Consumers ?? 0,
            Interlocked.Read(ref _cachedSamples), Interlocked.Read(ref _oiValues), _replacements,
            Volatile.Read(ref _backgroundTasks) + (gateway?.ReaderTasks ?? 0), dropped, recorderState,
            Volatile.Read(ref _lastError));
    }
}
