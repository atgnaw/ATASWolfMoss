using System.Collections;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class SharedOptionFlowTests
{
    private static readonly DateTime Start = new(2026, 9, 8, 13, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Expiration = new(2026, 9, 8);
    private static readonly OptionTradingSegment[] Segments = [new(Start, Start.AddHours(6))];
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static OptionFlowGroupKey Key(OptionFlowBucketMode mode = OptionFlowBucketMode.PreviousCompletedFixed, int minutes = 5)
        => new("offline:4001:2210", "QQQ", Expiration, mode, minutes, OptionFlowTradeScope.RegularTrades, 21);
    private static OptionContractDescriptor C(long id, decimal strike = 710)
        => new(id, "QQQ", Expiration, strike, OptionRight.Call, "QQQ", "SMART", 100, "");
    private static OptionCumulativeSample S(long id, double seconds, long volume, bool trade = false)
        => new(id, Start.AddSeconds(seconds), volume, 2, 100, trade ? 2m : null, trade ? 1L : null);
    private static OptionFlowGroup Book(OptionFlowBucketMode mode, int minutes = 5)
    {
        var b = new OptionFlowGroup(Key(mode, minutes), Start);
        b.Configure([C(1), C(2, 711)], 710, Segments, 1, Start);
        b.ObservationChanged(1, Start);
        return b;
    }
    private static void Add(OptionFlowGroup b, double seconds, long volume, bool trade = false, long id = 1)
        => Check(b.Receive(S(id, seconds, volume, trade), Start.AddSeconds(seconds)), "Expected accepted sample");

    public static void IdentityAndLifetime()
    {
        var key = Key();
        var a = SharedOptionFlow.Acquire(key, Start);
        var b = SharedOptionFlow.Acquire(key, Start.AddSeconds(10));
        Check(ReferenceEquals(a.Book, b.Book), "Identical settings reuse the book");
        a.Book.Configure([C(1)], 710, Segments, 1, Start);
        a.Observe(true, Start); b.Observe(true, Start.AddSeconds(10));
        Add(a.Book, 20, 100); Add(b.Book, 40, 120);
        var frame = a.Book.Read(Start.AddMinutes(5).AddSeconds(3), false);
        Check(frame.Rows[0].CallVolume == 20, "New member reuses existing baseline");
        Check(ReferenceEquals(frame, b.Book.Read(Start.AddMinutes(5).AddSeconds(3.5), false)), "Same immutable published snapshot");
        foreach (var other in new[] { key with { Connection = "other" }, key with { Ticker = "SPX" },
            key with { Expiration = Expiration.AddDays(1) }, key with { Mode = OptionFlowBucketMode.Rolling },
            key with { Minutes = 1 }, key with { Scope = OptionFlowTradeScope.AllTimeAndSales }, key with { Levels = 5 } })
        {
            using var separate = SharedOptionFlow.Acquire(other, Start);
            Check(!ReferenceEquals(a.Book, separate.Book), "Every semantic key component isolates statistics");
        }
        a.Observe(false, Start.AddMinutes(6)); a.Dispose();
        Add(b.Book, 361, 121); // Remaining observer keeps receiving.
        var old = b.Book; b.Dispose();
        using var fresh = SharedOptionFlow.Acquire(key, Start.AddMinutes(7));
        Check(!ReferenceEquals(old, fresh.Book) && fresh.Book.CachedSamples == 0, "Last release drops all group state");
    }

    public static void DedupAndPublicationOrdering()
    {
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var b = Book(mode);
            for (var i = 1; i <= 1000; i++)
            {
                var sample = S(1, i / 10d, i);
                Check(b.Receive(sample, sample.SampleUtc), "First callback accepted");
                Check(!b.Receive(sample, sample.SampleUtc), "Second chart callback is deduplicated");
            }
            b.Read(Start.AddSeconds(101), false);
            // A queued callback's captured receive time can precede publication.
            Check(b.Receive(S(1, 100.5, 1001), Start.AddSeconds(100.6)), "Queued callback does not reset shared samples");
            b.Read(Start.AddSeconds(100.9), false);
            Check(b.AcceptedSamples == 1001 && b.CachedSamples == 1001, "One sample copy per group, no capture-order clearing");
            Check(!b.Receive(S(1, 105, 1002), Start.AddSeconds(104)), "Future-source data rejected");
        }
    }

    public static void FixedIntervalsAndSegments()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var b = Book(OptionFlowBucketMode.PreviousCompletedFixed, minutes);
            Add(b, 10, 100); Add(b, 20, 120);
            Check(b.Read(Start.AddMinutes(minutes).AddSeconds(2), false).BucketEndUtc == null, "3-second publication grace");
            var frame = b.Read(Start.AddMinutes(minutes).AddSeconds(3), false);
            Check(frame.BucketStartUtc == Start && frame.BucketEndUtc == Start.AddMinutes(minutes), "All four interval boundaries");
            Check(frame.Rows[0].CallVolume == 20 && frame.Rows[0].CallFlowIsPartial, "Late cumulative baseline with no last-trade fields");
            Check(frame.Rows[1].CallFlowCoverage == OptionFlowCoverage.NoEvents, "No event remains unknown");
            var next = Start.AddMinutes(minutes + 2);
            var seg = new[] { new OptionTradingSegment(Start, Start.AddMinutes(minutes).AddSeconds(20)), new OptionTradingSegment(next, next.AddHours(1)) };
            b.Configure([C(1), C(2, 711)], 710, seg, 2, Start.AddMinutes(minutes).AddSeconds(10));
            Check(!b.Receive(S(1, minutes * 60 + 40, 9999), Start.AddMinutes(minutes + 1)), "Maintenance-gap events rejected");
            b.Configure([C(1), C(2, 711)], 710, seg, 2, next);
            Add(b, (minutes + 2) * 60 + 10, 10000);
            Add(b, (minutes + 2) * 60 + 20, 10003);
            var reopened = b.Read(next.AddMinutes(minutes).AddSeconds(3), false);
            Check(reopened.Rows[0].CallVolume == 3, "Never bridge trading segments or a residual short bucket");
        }
    }

    public static void FixedReceptionGaps()
    {
        var b = Book(OptionFlowBucketMode.PreviousCompletedFixed);
        Add(b, 10, 100); Add(b, 30, 110);
        b.Retire(1, Start.AddMinutes(1));
        b.Configure([C(2, 711), C(3, 712)], 711, Segments, 2, Start.AddMinutes(1));
        Check(!b.Receive(S(1, 90, 9000), Start.AddSeconds(90)), "Old range stops receiving immediately");
        b.Configure([C(1), C(2, 711)], 710, Segments, 3, Start.AddMinutes(2));
        Add(b, 130, 10000); Add(b, 140, 10005);
        b.Configure([C(2, 711), C(3, 712)], 712, Segments, 2, Start.AddMinutes(3));
        var frame = b.Read(Start.AddMinutes(5).AddSeconds(3), false);
        Check(frame.AtmStrikeUsd == 710 && frame.Rows.Select(r => r.StrikeUsd).SequenceEqual(new[] { 710m, 711m }), "Fixed bucket keeps original statistics ladder; stale revisions ignored");
        Check(frame.Rows[0].CallVolume == 15 && frame.Rows[0].CallPremium == 3000 && frame.Rows[0].CallFlowIsPartial,
            "Preserve 10 old + 5 new confirmed contracts; exclude 9890 unobserved volume");
        // A fence notification followed by identical local contracts must reactivate.
        b.Retire(1, Start.AddMinutes(6));
        b.Configure([C(1), C(2, 711)], 710, Segments, 4, Start.AddMinutes(6).AddSeconds(1));
        Add(b, 362, 11000);
    }

    public static void RollingIntervalsAndReentry()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var b = Book(OptionFlowBucketMode.Rolling, minutes);
            Add(b, 1, 100); Add(b, 4, 110);
            b.Configure([C(2, 711), C(3, 712)], 711, Segments, 2, Start.AddSeconds(10));
            var retained = b.Read(Start.AddSeconds(12), false).Rows.Single(r => r.StrikeUsd == 710);
            Check(retained.CallVolume == 10 && retained.CallFlowRetained, "Exited range retains confirmed rolling segment");
            b.Configure([C(1), C(2, 711)], 710, Segments, 3, Start.AddSeconds(20));
            Add(b, 22, 10000); Add(b, 25, 10005);
            var reentry = b.Read(Start.AddSeconds(30), false).Rows.Single(r => r.StrikeUsd == 710);
            Check(reentry.CallVolume == 15 && reentry.CallFlowIsPartial, "Rolling reentry sums segments without bridging gap");
            b.Configure([C(2, 711), C(3, 712)], 711, Segments, 4, Start.AddSeconds(40));
            Check(!b.Read(Start.AddMinutes(minutes).AddSeconds(41), false).Rows.Any(r => r.StrikeUsd == 710), "Old rows expire naturally for 1/3/5/10m");
        }
    }

    public static void ObservationGapsAndNewGroups()
    {
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var b = Book(mode);
            Add(b, 10, 100); Add(b, 20, 110);
            b.ObservationChanged(-1, Start.AddSeconds(30));
            Check(!b.Receive(S(1, 40, 1000), Start.AddSeconds(40)), "Suspended group rejects observations");
            b.ObservationChanged(1, Start.AddSeconds(50));
            Add(b, 60, 1000); Add(b, 70, 1005);
            var at = mode == OptionFlowBucketMode.Rolling ? Start.AddSeconds(80) : Start.AddMinutes(5).AddSeconds(3);
            Check(b.Read(at, false).Rows[0].CallVolume == 15, "Reconnect preserves observed portions, not gap delta");
            var newGroup = new OptionFlowGroup(Key(mode), Start.AddMinutes(2));
            newGroup.Configure([C(1)], 710, Segments, 1, Start.AddMinutes(2));
            newGroup.ObservationChanged(1, Start.AddMinutes(2));
            Check(!newGroup.Receive(S(1, 70, 1005), Start.AddMinutes(2)), "New settings group does not backfill old samples");
        }
    }

    public static void ClockResetAndBoundedStorage()
    {
        var b = Book(OptionFlowBucketMode.PreviousCompletedFixed, 1);
        b.ObserveClock(Start, 0, 1000);
        Add(b, 10, 100); Add(b, 20, 110);
        b.Read(Start.AddMinutes(1).AddSeconds(3), false);
        b.ObserveClock(Start.AddSeconds(-60), 64000, 1000);
        Check(b.CachedSamples == 0 && b.Read(Start.AddSeconds(-60), false).Rows.Count == 0, "Clock correction removes future cache and shared baselines");
        b = Book(OptionFlowBucketMode.PreviousCompletedFixed, 1);
        for (var seconds = 1; seconds <= 3600; seconds++)
        { Add(b, seconds, seconds); b.Read(Start.AddSeconds(seconds), false); }
        Check(b.CachedSamples <= 183, "Storage bounded by retention duration, not runtime");
    }

    public static void ActualPublisherUsesSharedBook()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var a = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 1, "QQQ", true);
        var b = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 1, "QQQ", true);
        var type = a.Indicator.GetType();
        object Get(object o, string n) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
        object EnumValue(string name, int value) => Enum.ToObject(assembly.GetType("WolfMoss.ATAS.PriceMapping.Core." + name)!, value);
        var now = new DateTime(2026, 9, 1, 14, 59, 0, DateTimeKind.Utc);
        var keyType = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.OptionFlowGroupKey")!;
        var key = Activator.CreateInstance(keyType, "production-offline", "QQQ", new DateOnly(2026, 9, 1),
            EnumValue("OptionFlowBucketMode", 0), 1, EnumValue("OptionFlowTradeScope", 0), 21)!;
        var registry = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.SharedOptionFlow")!;
        object Lease() => registry.GetMethod("Acquire")!.Invoke(null, [key, now])!;
        var leaseA = Lease(); var leaseB = Lease();
        try
        {
            var book = leaseA.GetType().GetProperty("Book")!.GetValue(leaseA)!;
            var contracts = Get(a.Indicator, "_activeOptionContracts");
            var segments = type.GetMethod("GetCachedFlowSegments", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(a.Indicator, ["QQQ", contracts]);
            book.GetType().GetMethod("Configure")!.Invoke(book, [contracts, 710m, segments, 1L, now]);
            foreach (var lease in new[] { leaseA, leaseB }) lease.GetType().GetMethod("Observe")!.Invoke(lease, [true, now]);
            type.GetField("_sharedFlow", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a.Indicator, leaseA);
            type.GetField("_sharedFlow", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(b.Indicator, leaseB);
            var sampleType = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.OptionCumulativeSample")!;
            foreach (var indicator in new[] { a.Indicator, b.Indicator })
            foreach (var seconds in new[] { 10, 20 })
                type.GetMethod("ReceiveFlowSample", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(indicator,
                    [Activator.CreateInstance(sampleType, 1L, now.AddSeconds(seconds), (long)seconds, 2m, 100m, null, null), now.AddSeconds(seconds), EnumValue("OptionFlowTradeScope", 0)]);
            a.PublishAt(now.AddSeconds(63)); b.PublishAt(now.AddSeconds(63.5));
            var fa = Get(a.Indicator, "_optionFlowSnapshot"); var fb = Get(b.Indicator, "_optionFlowSnapshot");
            Check(ReferenceEquals(fa, fb), "Actual two indicator publishers use the same immutable snapshot");
            Check((long)book.GetType().GetProperty("AcceptedSamples", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(book)! == 2,
                "Actual chart callbacks deduplicated by common book");
            var row = ((IEnumerable)fa.GetType().GetProperty("Rows")!.GetValue(fa)!).Cast<object>().First();
            Check((long?)row.GetType().GetProperty("CallVolume")!.GetValue(row) == 10, "Production result from shared samples, not fixture local buffers");
            foreach (var indicator in new[] { a.Indicator, b.Indicator })
                type.GetMethod("ReceiveFlowSample", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(indicator,
                    [Activator.CreateInstance(sampleType, 1L, now.AddSeconds(63.4), 30L, 2m, 100m, null, null), now.AddSeconds(63), EnumValue("OptionFlowTradeScope", 0)]);
            a.PublishAt(now.AddSeconds(63.5)); b.PublishAt(now.AddSeconds(63.7));
            var future = book.GetType().GetMethod("FutureSnapshot")!.Invoke(book, null)!;
            Check((long)future.GetType().GetProperty("Buffered")!.GetValue(future)! == 1
                && (long)future.GetType().GetProperty("Released")!.GetValue(future)! == 1,
                "Real callback/publication path drains one shared future event for two charts");
        }
        finally { ((IDisposable)leaseA).Dispose(); ((IDisposable)leaseB).Dispose(); }
    }

    public static void SharedHotPathAllocation()
    {
        var b = Book(OptionFlowBucketMode.PreviousCompletedFixed);
        var sample = S(1, 1, 100);
        b.Receive(sample, sample.SampleUtc);
        for (var i = 0; i < 1000; i++) b.Receive(sample, sample.SampleUtc);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) b.Receive(sample, sample.SampleUtc);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, $"Duplicate chart callbacks allocate no sample copy: {bytes}");
        var rolling = Book(OptionFlowBucketMode.Rolling, 5);
        rolling.SetReceiverBoundary(1, Start.AddMinutes(1));
        Check(rolling.Read(Start.AddSeconds(10), false).AtmLockedUntilUtc == Start.AddMinutes(1),
            "5-minute statistics report the common 1-minute receiver boundary, not a private lock");
        Console.WriteLine($"shared_flow_duplicate_callbacks: events=10000, allocated_bytes={bytes}");
    }

    public static void DetailedRejectionsAndFastConfiguration()
    {
        foreach (var mode in Enum.GetValues<OptionFlowBucketMode>())
        {
            var b = Book(mode);
            var d = new FlowReceptionDiagnostics();
            void Receive(OptionCumulativeSample s, DateTime at) => d.Record(s.SampleUtc, at, b.ReceiveDetailed(s, at), null);
            Receive(S(1, 10, 100), Start.AddSeconds(10));
            Receive(S(1, 10, 100), Start.AddSeconds(11));
            Receive(S(1, 9, 99), Start.AddSeconds(12));
            Receive(S(1, 20, 110), Start.AddSeconds(18));
            var result = d.Read();
            Check(result.Accepted == 1 && result.Duplicates == 1 && result.Rejected == 2 && result.OutOfOrder == 1 && result.Future == 1,
                "Deduplication is not counted as a time/boundary rejection");
            b.ObservationChanged(-1, Start.AddSeconds(21));
            Check(b.ReceiveDetailed(S(1, 22, 120), Start.AddSeconds(22)) == FlowSampleDisposition.ObservationGap,
                "Closed observation distinguished from duplicate");
            var input = new[] { C(1), C(2, 711) };
            var clone = input.ToArray();
            b.Configure(input, 710, Segments, 2, Start.AddSeconds(22));
            b.Read(Start.AddSeconds(22), false);
            for (var i = 0; i < 100; i++) b.Configure(i % 2 == 0 ? input : clone, 710, Segments, 2, Start.AddSeconds(22));
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) b.Configure(i % 2 == 0 ? input : clone, 710, Segments, 2, Start.AddSeconds(22));
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Check(bytes == 0, $"Equivalent chart ladders must not allocate on steady configuration: {bytes}");
        }
    }
}
