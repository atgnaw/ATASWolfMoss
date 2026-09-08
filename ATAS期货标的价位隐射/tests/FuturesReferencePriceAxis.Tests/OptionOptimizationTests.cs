using System.Collections;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class OptionOptimizationTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 13, 30, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static object? Get(object owner, string field) => owner.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner);
    private static void Set(object owner, string field, object? value) => owner.GetType()
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);

    public static void BinaryAggregation()
    {
        var random = new Random(2210);
        for (var run = 0; run < 300; run++)
        {
            var samples = new List<OptionCumulativeSample>();
            var utc = Start;
            for (var i = 0; i < random.Next(1, 700); i++)
            {
                utc = utc.AddSeconds(random.Next(0, 8)); // include equal timestamps
                samples.Add(new(1, utc, i * 5, 2m, 100m,
                    i % 4 == 0 ? null : 2m, i % 4 == 0 ? null : 1L));
            }
            for (var query = 0; query < 60; query++)
            {
                var begin = Start.AddSeconds(random.Next(-20, 4000));
                var end = begin.AddSeconds(random.Next(0, 700));
                foreach (var partial in new[] { false, true })
                {
                    var expected = Linear(samples, begin, end, partial, out var a);
                    var actual = OptionFlowAggregation.TryCalculateBucketValue(samples, begin, end, partial, out var b);
                    Check(expected == actual && a == b, "Binary lookup must match original linear bucket semantics");
                }
            }
        }
    }
    private static bool Linear(IReadOnlyList<OptionCumulativeSample> samples, DateTime begin, DateTime end,
        bool partial, out OptionIntervalValue value)
    {
        value = OptionIntervalValue.Zero;
        if (end <= begin || samples.Count == 0) return false;
        OptionCumulativeSample? baseline = null, first = null, last = null;
        foreach (var sample in samples)
        {
            if (sample.SampleUtc < begin) { baseline = sample; continue; }
            if (sample.SampleUtc >= end) break;
            first ??= sample; last = sample;
        }
        if (!first.HasValue || !last.HasValue) return false;
        if (baseline.HasValue)
        {
            if (!OptionFlowAggregation.TryCalculateDelta(baseline.Value, last.Value, out var full)) return false;
            value = full with { ObservedStartUtc = begin, IsPartial = false };
            return true;
        }
        if (!partial || !first.Value.TryGetLastTradeValue(out var trade)
            || !OptionFlowAggregation.TryCalculateDelta(first.Value, last.Value, out var remaining)) return false;
        value = new(checked(trade.Volume + remaining.Volume), trade.Premium + remaining.Premium, first.Value.SampleUtc, true);
        return true;
    }

    public static void RingBuffer()
    {
        var buffer = new OptionSampleBuffer();
        var reference = new List<OptionCumulativeSample>();
        for (var i = 0; i < 30000; i++)
        {
            var sample = new OptionCumulativeSample(1, Start.AddSeconds(i), i, 2m, 100m);
            buffer.Add(sample); reference.Add(sample);
            if (i % 17 == 0 && reference.Count > 40)
            {
                buffer.RemovePrefix(31); reference.RemoveRange(0, 31);
            }
            if (i % 101 == 0)
            {
                Check(buffer.SequenceEqual(reference), "Ring ordering after wrap/growth/expiration");
                var cutoff = Start.AddSeconds(i - 20);
                Check(OptionFlowAggregation.LowerBound(buffer, cutoff)
                    == reference.FindIndex(s => s.SampleUtc >= cutoff), "Binary search on wrapped ring");
            }
        }
        buffer.Clear();
        Check(buffer.Count == 0, "Ring reset");
        buffer.Add(new(1, Start, 0, 2, 100));
        Check(buffer[0].TotalVolume == 0, "Ring reusable after reset");
    }

    public static void RollingDifferential()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        foreach (var scope in Enum.GetValues<OptionFlowTradeScope>())
        {
            var current = new OptionRollingFlowState();
            var old = new ReferenceRollingFlowState();
            var contracts = Enumerable.Range(1, 3).Select(id => new OptionContractDescriptor(id, "SPX",
                DateOnly.FromDateTime(Start), 7700 + id * 5, OptionRight.Call, "SPXW", "SMART", 100m, "")).ToArray();
            var segments = new[] { new OptionTradingSegment(Start, Start.AddMinutes(30)),
                new OptionTradingSegment(Start.AddMinutes(40), Start.AddMinutes(100)) };
            current.SetTradingSegments(segments, Start); old.SetTradingSegments(segments, Start);
            current.ConfigureLadder(contracts[..2], 7710, Start, minutes);
            old.ConfigureLadder(contracts[..2], 7710, Start, minutes);
            for (var second = 1; second < 6300; second++)
            {
                var now = Start.AddSeconds(second);
                if (second % 137 == 0)
                {
                    var ladder = (second / 137) % 2 == 0 ? contracts[..2] : contracts[1..];
                    current.ConfigureLadder(ladder, 7710, now, minutes);
                    old.ConfigureLadder(ladder, 7710, now, minutes);
                }
                if (second % 211 == 0) { current.Suspend(now); old.Suspend(now); }
                if (second % 211 == 15) { current.Resume(now); old.Resume(now); }
                var id = second % 3 + 1;
                var sample = new OptionCumulativeSample(id, now, (second % 800) * 3,
                    2, 100, second % 4 == 0 ? null : 2, second % 4 == 0 ? null : 1);
                Check(current.Add(sample, scope, now) == old.Add(sample, scope, now), "Same event acceptance");
                if (second % 11 != 0) continue;
                var a = current.CreateSnapshot("SPX", contracts[0].Expiration, now, minutes, scope, 21, false);
                var b = old.CreateSnapshot("SPX", contracts[0].Expiration, now, minutes, scope, 21, false);
                Check(a.Rows.SequenceEqual(b.Rows), "Rolling row values, coverage and retained bounds unchanged");
                Check((a with { Rows = Array.Empty<OptionStrikeRow>() }) == (b with { Rows = Array.Empty<OptionStrikeRow>() }),
                    "Rolling metadata and session states unchanged");
            }
        }
    }

    public static void RenderCache()
    {
        var rows = new[] { new OptionStrikeRow(101, 10, null, 100, null, 1, null,
                CallFlowIsPartial: true, PutFlowCoverage: OptionFlowCoverage.NoEvents,
                FlowLowerBoundUsd: 100.5m, FlowUpperBoundUsd: 101.5m),
            new OptionStrikeRow(100, 0, 20, 0, 200, 0, 2) };
        var cache = new OptionRenderCache();
        var frame = cache.Get(rows, false, OptionDataStatus.Live);
        Check(frame.Strikes.SequenceEqual(new[] { 100m, 101m }) && frame.Maximum == 200, "Sorted shared scale");
        Check(frame.Rows[1].Lower == 100.5m && frame.Rows[1].Upper == 101.5m, "Retained explicit row bounds");
        Check(frame.Rows[1].CallLabel == OptionPresentation.FormatFlowValue(100, true)
            && !frame.Rows[1].CallLabel.Contains('$'), "Partial marker and no dollar symbol");
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) cache.Get(rows, false, OptionDataStatus.Live);
        Check(GC.GetAllocatedBytesForCurrentThread() == bytes && cache.BuildCount == 1, "Stable render data cache does not allocate");
        Check(!ReferenceEquals(frame, cache.Get(rows, false, OptionDataStatus.Warming)), "Status invalidates missing labels");
        Check(!ReferenceEquals(frame, cache.Get(rows.ToArray(), false, OptionDataStatus.Live)), "Replacement frame invalidates");
        Check(cache.Get(rows, true, OptionDataStatus.Daily).Maximum == 20, "OI and Flow scales remain independent");
    }

    public static void PublicationAndStatus()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 5, "QQQ", true);
        var indicator = fixture.Indicator;
        var now = new DateTime(2026, 9, 1, 15, 0, 4, DateTimeKind.Utc);
        fixture.PublishAt(now);
        var original = Get(indicator, "_optionFlowSnapshot")!;
        fixture.PublishAt(now.AddSeconds(10));
        Check(ReferenceEquals(original, Get(indicator, "_optionFlowSnapshot")), "Fixed completed bucket reuses immutable frame");
        fixture.PublishAt(now.AddMinutes(5));
        Check(!ReferenceEquals(original, Get(indicator, "_optionFlowSnapshot")), "New completed bucket invalidates cache");
        Set(indicator, "_showOptionOpenInterest", true);
        var oi = (IDictionary)Get(indicator, "_optionOpenInterest")!;
        oi[1L] = 123L; Set(indicator, "_oiRevision", 1L);
        fixture.PublishAt(now.AddMinutes(5));
        var firstOi = Get(indicator, "_optionOpenInterestSnapshot");
        fixture.PublishAt(now.AddMinutes(5).AddSeconds(1));
        Check(ReferenceEquals(firstOi, Get(indicator, "_optionOpenInterestSnapshot")), "Unchanged OI reuses immutable snapshot");
        oi[1L] = 456L; Set(indicator, "_oiRevision", (long)Get(indicator, "_oiRevision")! + 1L);
        fixture.PublishAt(now.AddMinutes(5).AddSeconds(2));
        Check(!ReferenceEquals(firstOi, Get(indicator, "_optionOpenInterestSnapshot")), "OI correction invalidates at same received time");
        var firstRow = ((IEnumerable)firstOi!.GetType().GetProperty("Rows")!.GetValue(firstOi)!).Cast<object>().First();
        Check((long)firstRow.GetType().GetProperty("CallOpenInterest")!.GetValue(firstRow)! == 123L, "Old snapshot remains immutable");

        var type = indicator.GetType();
        var cache = type.GetMethod("CacheEditionStatusLines", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var read = type.GetMethod("TryGetCachedEditionStatusLines", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var visibility = Enum.ToObject(cache.GetParameters()[0].ParameterType, 0);
        var perf = new[] { "PERF" };
        var flow = Get(indicator, "_optionFlowSnapshot");
        var latestOi = Get(indicator, "_optionOpenInterestSnapshot");
        var snapshots = new object?[] { null, null, latestOi, flow };
        cache.Invoke(indicator, new object?[] { visibility, snapshots, 800, 42,
            new List<string> { "cached" }, perf });
        object?[] args = { visibility, snapshots, perf, 800, 42, null };
        Check((bool)read.Invoke(indicator, args)!, "Status cache includes option columns");
        args[3] = 600;
        Check(!(bool)read.Invoke(indicator, args)!, "Chart height invalidates diagnostic clipping");
        args[3] = 800; args[4] = 40;
        Check(!(bool)read.Invoke(indicator, args)!, "Changing active lines invalidates status");
        args[4] = 42; args[2] = new[] { "PERF updated" };
        Check(!(bool)read.Invoke(indicator, args)!, "Performance updates invalidate status");
    }

    public static void PublicationBoundariesAndAllocations()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed,
            5, "QQQ", true, "20260901:0930-20260901:1101");
        var now = new DateTime(2026, 9, 1, 15, 0, 4, DateTimeKind.Utc);
        fixture.PublishAt(now);
        var live = Get(fixture.Indicator, "_optionFlowSnapshot")!;
        string State(object snapshot) => snapshot.GetType().GetProperty("Status")!.GetValue(snapshot)!.ToString()!;
        Check(State(live) == "Live", "Before segment close");
        Set(fixture.Indicator, "_optionDataIsDelayed", true);
        fixture.PublishAt(now.AddSeconds(1));
        Check(State(Get(fixture.Indicator, "_optionFlowSnapshot")!) == "Delayed", "Cached bucket reflects delayed feed");
        Set(fixture.Indicator, "_optionDataIsDelayed", false);
        fixture.PublishAt(now.AddMinutes(1));
        var closed = Get(fixture.Indicator, "_optionFlowSnapshot")!;
        Check(State(closed) == "Closed", "Same completed bucket changes to closed after partial segment tail");
        Check(ReferenceEquals(live.GetType().GetProperty("Rows")!.GetValue(live),
            closed.GetType().GetProperty("Rows")!.GetValue(closed)), "State-only transition retains data rows");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) fixture.PublishAt(now.AddMinutes(1));
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes < 100000, "Repeated publish cannot copy the 60,480-sample history");
        Console.WriteLine($"fixed_republish: iterations=100, allocated_bytes={bytes}");
    }
}
