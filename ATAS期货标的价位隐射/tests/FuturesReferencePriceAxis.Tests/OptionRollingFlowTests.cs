using WolfMoss.ATAS.PriceMapping.Core;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal static class OptionRollingFlowTests
{
    private static readonly DateOnly Expiration = new(2026, 9, 1);
    private static readonly OptionFlowTradeScope Regular = OptionFlowTradeScope.RegularTrades;
    private static DateTime T(int minute, int second = 0)
        => new DateTime(2026, 9, 1, 13, 30, 0, DateTimeKind.Utc).AddMinutes(minute).AddSeconds(second);
    private static OptionContractDescriptor C(long id, decimal strike)
        => new(id, "QQQ", Expiration, strike, OptionRight.Call, "QQQ", "SMART", 100m, "");
    private static OptionCumulativeSample S(long id, int minute, int second, long volume,
        long size = 1) => new(id, T(minute, second), volume, 2m, 100m, 2m, size);
    private static OptionRollingFlowState Book(params OptionContractDescriptor[] contracts)
    {
        var book = new OptionRollingFlowState();
        book.SetTradingSegments(new[] { new OptionTradingSegment(T(0), T(60)) }, T(0));
        book.ConfigureLadder(contracts, 100m, T(0), 5);
        return book;
    }
    private static OptionFlowSnapshot Snapshot(OptionRollingFlowState book, DateTime time,
        int minutes = 5, OptionFlowTradeScope? scope = null)
        => book.CreateSnapshot("QQQ", Expiration, time, minutes, scope ?? Regular, 21, false);
    private static void Add(OptionRollingFlowState book, OptionCumulativeSample sample)
        => Check(book.Add(sample, Regular, sample.SampleUtc));
    private static OptionStrikeRow Row(OptionFlowSnapshot snapshot, decimal strike)
        => snapshot.Rows.Single(row => row.StrikeUsd == strike);

    public static void LockAndRetention()
    {
        var book = Book(C(1, 99), C(2, 100));
        Check(book.IsLadderLocked(T(4, 59)));
        Check(!book.IsLadderLocked(T(5)));
        Add(book, S(1, 4, 0, 100, 2));
        Add(book, S(1, 4, 40, 103, 3));
        Add(book, S(2, 0, 1, 10));
        Add(book, S(2, 4, 40, 20));
        book.ConfigureLadder(new[] { C(2, 100), C(3, 101) }, 101m, T(5), 5);
        Check(book.IsLadderLocked(T(9, 59)));
        Check(!book.IsLadderLocked(T(10)));
        Check(!book.Add(S(1, 5, 10, 999, 99), Regular, T(5, 10)));
        Add(book, S(3, 5, 15, 1000, 4));
        var afterSwitch = Snapshot(book, T(5, 30));
        Equal(101m, afterSwitch.AtmStrikeUsd);
        Equal(T(10), afterSwitch.AtmLockedUntilUtc);
        Equal(3, afterSwitch.Rows.Count);
        Equal(2, afterSwitch.ActiveStrikeCount);
        Equal(1, afterSwitch.RetainedStrikeCount);
        Equal(5L, Row(afterSwitch, 99).CallVolume);
        Check(Row(afterSwitch, 99).CallFlowRetained);
        Check(Row(afterSwitch, 99).CallFlowIsPartial);
        Equal(10L, Row(afterSwitch, 100).CallVolume);
        Check(!Row(afterSwitch, 100).CallFlowIsPartial);
        Equal(4L, Row(afterSwitch, 101).CallVolume);
        Check(Row(afterSwitch, 101).CallFlowIsPartial);
        Equal(3L, Row(Snapshot(book, T(9, 20)), 99).CallVolume);
        Check(!Snapshot(book, T(9, 41)).Rows.Any(row => row.StrikeUsd == 99));
        book.RenewAtmLock(101m, T(10), 5);
        Check(book.IsLadderLocked(T(14, 59)));
        Equal(T(15), Snapshot(book, T(10)).AtmLockedUntilUtc);

        // A large jump must not stretch the retained row across empty strikes.
        var jump = Book(C(1, 100), C(2, 101));
        Add(jump, S(1, 4, 0, 100));
        jump.ConfigureLadder(new[] { C(3, 200), C(4, 201) }, 200, T(5), 5);
        var retained = Row(Snapshot(jump, T(5, 30)), 100);
        Equal(99.5m, retained.FlowLowerBoundUsd);
        Equal(100.5m, retained.FlowUpperBoundUsd);
        var active = Row(Snapshot(jump, T(5, 30)), 200);
        Equal(199.5m, active.FlowLowerBoundUsd);
        Equal(200.5m, active.FlowUpperBoundUsd);
    }

    public static void LateBaselinesAndObservationGaps()
    {
        var book = Book(C(1, 100), C(2, 101));
        Add(book, S(1, 3, 0, 1000, 2));
        Add(book, S(1, 4, 0, 1005, 1));
        var partial = Row(Snapshot(book, T(5)), 100);
        Equal(7L, partial.CallVolume);
        Equal(1400m, partial.CallPremium);
        Equal(T(3), partial.CallFlowObservedStartUtc);
        Equal(OptionFlowCoverage.Partial, partial.CallFlowCoverage);
        Equal(OptionFlowCoverage.NoEvents, Row(Snapshot(book, T(5)), 101).CallFlowCoverage);

        // Re-enter before old observations expire: never subtract across the gap.
        book.ConfigureLadder(new[] { C(2, 101) }, 101, T(5), 5);
        book.ConfigureLadder(new[] { C(1, 100), C(2, 101) }, 100, T(6), 5);
        Check(!book.Add(S(1, 5, 59, 5000, 500), Regular, T(6)));
        Add(book, S(1, 6, 5, 5003, 3));
        var reentered = Row(Snapshot(book, T(6, 10)), 100);
        Equal(10L, reentered.CallVolume);
        Check(reentered.CallFlowIsPartial);
        book.Suspend(T(6, 15));
        Check(!book.Add(S(1, 6, 20, 9000, 1000), Regular, T(6, 20)));
        book.Resume(T(6, 30));
        Add(book, S(1, 6, 35, 10001, 1));
        Equal(11L, Row(Snapshot(book, T(6, 40)), 100).CallVolume);

        var missing = Book(C(3, 102));
        var baselineOnly = new OptionCumulativeSample(3, T(2), 100, 2m, 100m);
        Add(missing, baselineOnly);
        var noData = Row(Snapshot(missing, T(3)), 102);
        Equal(OptionFlowCoverage.NoData, noData.CallFlowCoverage);
        Equal("?", OptionPresentation.GetFlowMissingMarker(OptionDataStatus.Live, noData.CallFlowCoverage));
        Equal("NO DATA", OptionPresentation.GetFlowCoverageLabel(
            OptionDataStatus.Live, false, false, noData.CallFlowCoverage));
        Add(missing, baselineOnly with { SampleUtc = T(3), TotalVolume = 105 });
        var recovered = Row(Snapshot(missing, T(4)), 102);
        Equal(5L, recovered.CallVolume);
        Check(recovered.CallFlowIsPartial);
        Equal("1.4K*", OptionPresentation.FormatFlowValue(1400m, true));
    }

    public static void SessionBoundaries()
    {
        var book = Book(C(1, 100));
        book.SetTradingSegments(new[]
        {
            new OptionTradingSegment(T(0), T(10)),
            new OptionTradingSegment(T(12), T(20))
        }, T(0));
        book.RenewAtmLock(100, T(9), 5);
        Equal(T(10), Snapshot(book, T(9)).AtmLockedUntilUtc);
        Add(book, S(1, 8, 0, 100, 2));
        Add(book, S(1, 9, 0, 104, 1));
        var closed = Snapshot(book, T(11));
        Equal(T(10), closed.BucketEndUtc);
        Equal(T(5), closed.BucketStartUtc);
        Equal(OptionDataStatus.Closed, closed.Status);
        Check(!book.Add(S(1, 10, 0, 120), Regular, T(10)));
        Check(!book.Add(S(1, 11, 0, 150), Regular, T(11)));
        book.AdvanceClock(T(12));
        Check(!book.IsLadderLocked(T(12)));
        book.RenewAtmLock(100, T(12), 5);
        Add(book, S(1, 12, 10, 1001, 1));
        var reopened = Snapshot(book, T(12, 20));
        Equal(T(12), reopened.BucketStartUtc);
        Equal(1L, Row(reopened, 100).CallVolume);
        Equal(OptionDataStatus.Partial, reopened.Status);
        Check(!book.Add(S(1, 9, 59, 999), Regular, T(12, 21)));
        var staleRefresh = Snapshot(book, T(11, 59));
        Equal(T(12), staleRefresh.BucketStartUtc);
        Equal(1L, Row(staleRefresh, 100).CallVolume);

        var delayedOpen = Book(C(1, 100));
        delayedOpen.SetTradingSegments(new[] { new OptionTradingSegment(T(12), T(20)) }, T(0));
        // Already prepared before the opening: receive the opening event late.
        Check(delayedOpen.Add(S(1, 12, 1, 1000, 2), Regular, T(12, 2)));
        Equal(2L, Row(Snapshot(delayedOpen, T(12, 3)), 100).CallVolume);
        delayedOpen.Suspend(T(12, 4));
        delayedOpen.SetTradingSegments(new[] { new OptionTradingSegment(T(13), T(20)) }, T(13));
        Check(!delayedOpen.Add(S(1, 13, 1, 2000, 100), Regular, T(13, 2)));
        delayedOpen.Resume(T(13, 3));
        Check(!delayedOpen.Add(S(1, 13, 2, 2000, 100), Regular, T(13, 4)));
        Add(delayedOpen, S(1, 13, 5, 2001, 1));
        Equal(1L, Row(Snapshot(delayedOpen, T(13, 6)), 100).CallVolume);

        // Use actual parsed GTH/RTH segments, including a break and DST offsets.
        foreach (var date in new[] { "20260901", "20261201" })
        {
            var segments = IbTradingHoursParser.ParseEastern(
                $"{date}:0400-{date}:0900;{date}:0930-{date}:1600", true);
            Equal(2, segments.Count);
            var spx = new OptionRollingFlowState();
            spx.SetTradingSegments(segments, segments[0].StartUtc);
            spx.ConfigureLadder(new[] { C(1, 100) }, 100, segments[0].StartUtc, 3);
            var duringBreak = spx.CreateSnapshot("SPX", Expiration,
                segments[0].EndUtc.AddMinutes(2), 3, Regular, 21, false);
            Equal(segments[0].EndUtc, duringBreak.BucketEndUtc);
            var rth = spx.CreateSnapshot("SPX", Expiration,
                segments[1].StartUtc.AddMinutes(1), 3, Regular, 21, false);
            Equal(segments[1].StartUtc, rth.BucketStartUtc);
            Equal(OptionDataStatus.Warming, rth.Status);
        }
    }

    public static void AllIntervalsAndReset()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var book = Book(C(1, 100));
            book.RenewAtmLock(100, T(0), minutes);
            Add(book, S(1, 0, 0, 100, 1));
            Add(book, S(1, minutes, 0, 110, 2));
            var snapshot = Snapshot(book, T(minutes, 1), minutes);
            Equal(minutes, snapshot.IntervalMinutes);
            Equal(T(0, 1), snapshot.BucketStartUtc);
            Equal(T(minutes, 1), snapshot.BucketEndUtc);
            Equal(10L, Row(snapshot, 100).CallVolume);
            Check(!Row(snapshot, 100).CallFlowIsPartial);
            Check(!book.IsLadderLocked(T(minutes)));
            Equal(OptionFlowCoverage.NoEvents,
                Row(Snapshot(book, T(minutes, 1), minutes, OptionFlowTradeScope.AllTimeAndSales),
                    100).CallFlowCoverage);
            // End-exclusive events appear on the next refresh.
            var boundary = Book(C(1, 100));
            Add(boundary, S(1, 0, 0, 100, 1));
            Add(boundary, S(1, minutes, 0, 110, 2));
            Equal(1L, Row(Snapshot(boundary, T(minutes), minutes), 100).CallVolume);
            Equal(10L, Row(Snapshot(boundary, T(minutes, 1), minutes), 100).CallVolume);
            Check(!book.Add(S(1, 0, 0, 500, 100), Regular, T(minutes, 1)));
            Equal(10L, Row(Snapshot(book, T(minutes, 2), minutes), 100).CallVolume);
            Add(book, S(1, minutes, 3, 1, 1));
            Equal(1L, Row(Snapshot(book, T(minutes, 4), minutes), 100).CallVolume);
            Check(Row(Snapshot(book, T(minutes, 4), minutes), 100).CallFlowIsPartial);
            book.Clear();
            Equal(0, Snapshot(book, T(minutes, 5), minutes).Rows.Count);
            Check(!book.IsLadderLocked(T(minutes, 5)));
        }
    }

    public static void IndicatorSnapshotPath()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var dll = Path.Combine(root, "src", "FuturesReferencePriceAxis.DealerHeatmap",
#if DEBUG
            "bin", "Debug",
#else
            "bin", "Release",
#endif
            "FuturesReferencePriceAxis.DealerHeatmap.dll");
        Assembly? ResolveAtas(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(@"C:\Program Files (x86)\ATAS Platform", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += ResolveAtas;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            Type Core(string name) => assembly.GetType("WolfMoss.ATAS.PriceMapping.Core." + name, true)!;
            var indicatorType = assembly.GetType(
                "WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!;
            // No constructor, ATAS initialization, Gateway or network is involved.
            var indicator = RuntimeHelpers.GetUninitializedObject(indicatorType);
            void Set(string name, object value)
                => indicatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(indicator, value);
            var state = Activator.CreateInstance(Core("OptionRollingFlowState"))!;
            object? Call(string name, params object[] args)
                => state.GetType().GetMethod(name)!.Invoke(state, args);
            Array Single(string name, object value)
            {
                var result = Array.CreateInstance(Core(name), 1);
                result.SetValue(value, 0);
                return result;
            }
            var segment = Activator.CreateInstance(Core("OptionTradingSegment"), T(0), T(60))!;
            var contract = Activator.CreateInstance(Core("OptionContractDescriptor"),
                1L, "QQQ", Expiration, 100m, Enum.ToObject(Core("OptionRight"), 0),
                "QQQ", "SMART", 100m, "", "America/New_York")!;
            var contracts = Single("OptionContractDescriptor", contract);
            Call("SetTradingSegments", Single("OptionTradingSegment", segment), T(0));
            Call("ConfigureLadder", contracts, 100m, T(0), 1);
            var sample = Activator.CreateInstance(Core("OptionCumulativeSample"),
                1L, T(0, 30), 100L, 2m, 100m, 2m, 3L)!;
            Call("Add", sample, Enum.ToObject(Core("OptionFlowTradeScope"), 0), T(0, 30));
            Set("_optionDataSync", new object());
            Set("_rollingFlow", state);
            Set("_optionFlowBucketMode", Enum.ToObject(Core("OptionFlowBucketMode"), 1));
            Set("_optionFlowIntervalMinutes", 1);
            Set("_optionStrikeLevels", 21);
            var sampleList = typeof(List<>).MakeGenericType(Core("OptionCumulativeSample"));
            var samples = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(long), sampleList))!;
            var snapshot = indicatorType.GetMethod("CreateFlowSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(indicator,
                new object[] { "QQQ", Expiration, 100m, new decimal[] { 100m }, contracts,
                    samples, T(0, 40), T(0), false })!;
            object? Property(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item);
            Equal("Rolling", Property(snapshot, "BucketMode")!.ToString());
            Equal(1, (int)Property(snapshot, "IntervalMinutes")!);
            Equal(T(0), (DateTime)Property(snapshot, "BucketStartUtc")!);
            Equal(T(1), (DateTime)Property(snapshot, "AtmLockedUntilUtc")!);
            var rows = ((System.Collections.IEnumerable)Property(snapshot, "Rows")!).Cast<object>().ToArray();
            Equal(1, rows.Length);
            Equal(3L, (long)Property(rows[0], "CallVolume")!);
            Equal(true, (bool)Property(rows[0], "CallFlowIsPartial")!);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= ResolveAtas;
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Rolling flow assertion failed.");
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
