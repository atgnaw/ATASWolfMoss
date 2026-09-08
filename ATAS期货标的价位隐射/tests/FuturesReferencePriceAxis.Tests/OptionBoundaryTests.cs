using System.Collections;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class OptionBoundaryTests
{
    private static DateTime Et(int month, int day, int hour, int minute = 0)
        => NyseTradingCalendar.EasternToUtc(new DateTime(2026, month, day, hour, minute, 0));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object? Get(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] values)
    {
        try { return owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, values); }
        catch (TargetInvocationException error) { throw new InvalidOperationException(name + ": " + error.InnerException?.Message, error.InnerException); }
    }
    private static object? Prop(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "atas-oi-boundary-" + Guid.NewGuid().ToString("N"));
    private static void Clean(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    public static void Freshness()
    {
        Check(!OptionOiFreshnessPolicy.IsConfirmed(DateTime.MinValue, DateTime.MinValue)
            && OptionOiFreshnessPolicy.ConfirmationStartUtc(DateTime.MinValue) == DateTime.MinValue, "Uninitialized clock safe");
        Check(OptionOiFreshnessPolicy.ConfirmationStartUtc(Et(9, 7, 10)) == Et(9, 4, 8, 30), "Labor Day uses previous open day's epoch");
        Check(OptionOiFreshnessPolicy.NextRetryUtc(Et(9, 4, 10)) == Et(9, 8, 8, 30), "Retry skips weekend and Labor Day");
        Check(OptionOiFreshnessPolicy.ConfirmationStartUtc(Et(9, 8, 8, 29)) == Et(9, 4, 8, 30), "Premarket reference retained before confirmation boundary");
        Check(!OptionOiFreshnessPolicy.IsConfirmed(Et(9, 4, 9), Et(9, 8, 8, 30)), "Friday receipt not confirmed on Tuesday");
        Check(OptionOiFreshnessPolicy.IsConfirmed(Et(9, 8, 8, 30), Et(9, 8, 8, 31)), "New epoch confirmed");
        Check(!OptionOiFreshnessPolicy.IsConfirmed(Et(9, 8, 9), Et(9, 8, 8, 31)), "Future receipt rejected");
        Check(OptionOiFreshnessPolicy.NextRetryUtc(Et(9, 8, 8, 30)) == Et(9, 8, 9, 15), "Next scheduled slot");
        Check(OptionOiFreshnessPolicy.NextRetryUtc(Et(9, 8, 9, 15)) == Et(9, 8, 9, 35), "Last morning retry");
        Check(OptionOiFreshnessPolicy.NextRetryUtc(Et(3, 6, 10)) == Et(3, 9, 8, 30), "DST weekend retry");
        Check(OptionOiFreshnessPolicy.NextRetryUtc(Et(10, 30, 10)) == Et(11, 2, 8, 30), "Winter-time retry");
    }

    public static void CacheValidation()
    {
        var dir = NewDirectory(); Directory.CreateDirectory(dir);
        var expiration = new DateOnly(2026, 9, 8); var now = Et(9, 8, 10);
        var path = Path.Combine(dir, "QQQ-20260908.json");
        try
        {
            OptionOpenInterestCache.SaveEntries(dir, "QQQ", expiration, new Dictionary<long, OptionOiCacheEntry>
            { [1] = new(0, Et(9, 4, 9)), [2] = new(20, Et(9, 8, 9)) }, now);
            var loaded = OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now);
            Check(loaded.Values[1] == 0 && !loaded.Values.ContainsKey(3), "Real zero distinct from missing");
            Check(!OptionOiFreshnessPolicy.IsConfirmed(loaded.Entries[1].ReceivedUtc, now)
                && OptionOiFreshnessPolicy.IsConfirmed(loaded.Entries[2].ReceivedUtc, now), "Fresh contract does not refresh old contract's timestamp");
            Check(OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, Et(9, 9, 10)).Values.Count == 0, "Expired file ignored");
            File.WriteAllText(path, "{\"ticker\":\"QQQ\",\"expiration\":\"2026-09-08\",\"received_utc\":\"2026-09-08T13:00:00Z\",\"values\":[{\"con_id\":5,\"open_interest\":0}]}");
            Check(OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now).Values[5] == 0, "Legacy schema uses actual file receipt");
            foreach (var json in new[] { "{", "null", "{\"ticker\":\"QQQ\",\"expiration\":\"2026-09-08\",\"values\":null}",
                "{\"schema_version\":99,\"ticker\":\"QQQ\",\"expiration\":\"2026-09-08\",\"values\":[]}",
                "{\"ticker\":\"QQQ\",\"expiration\":\"2026-09-08\",\"received_utc\":\"2027-01-01T00:00:00Z\",\"values\":[{\"con_id\":1,\"open_interest\":1}]}" })
            {
                File.WriteAllText(path, json);
                Check(OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now).Values.Count == 0, "Malformed/future/unknown schema rejected");
            }
            using (var file = new FileStream(path, FileMode.Create)) file.SetLength(OptionOpenInterestCache.MaximumFileBytes + 1);
            Check(OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now).Values.Count == 0, "Oversized cache bounded");
            Check(OptionOpenInterestCache.LoadSnapshot(dir, "../QQQ", expiration, now).Values.Count == 0, "Ticker cannot escape cache directory");
        }
        finally { Clean(dir); }
    }

    public static void CacheMerge()
    {
        var dir = NewDirectory(); var expiration = new DateOnly(2026, 9, 8); var now = Et(9, 8, 10);
        try
        {
            Task.WhenAll(Enumerable.Range(1, 12).Select(id => Task.Run(async () =>
            {
                for (var retry = 0; ; retry++)
                {
                    try { OptionOpenInterestCache.Save(dir, "QQQ", expiration, now, new Dictionary<long, long> { [id] = id }); break; }
                    catch (IOException) when (retry < 100) { await Task.Delay(10); }
                }
            }))).GetAwaiter().GetResult();
            var loaded = OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now);
            Check(loaded.Values.Count == 12, "Concurrent subset writers preserve union");
            OptionOpenInterestCache.SaveEntries(dir, "QQQ", expiration, new Dictionary<long, OptionOiCacheEntry>
                { [1] = new(999, now.AddMinutes(-1)), [13] = new(13, now.AddMinutes(-1)) }, now);
            loaded = OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now);
            Check(loaded.Values[1] == 1 && loaded.Values[13] == 13, "Older writer cannot replace newer per-contract value");
            var path = Path.Combine(dir, "QQQ-20260908.json");
            using (var held = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = false;
                try { OptionOpenInterestCache.Save(dir, "QQQ", expiration, now, new Dictionary<long, long> { [1] = 999 }); }
                catch (IOException) { failed = true; }
                Check(failed && OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now).Values[1] == 1, "Busy writer does not corrupt original");
            }
            Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "Atomic-save temporary files cleaned");
            OptionOpenInterestCache.SaveEntries(dir, "QQQ", expiration,
                Enumerable.Range(1, 5000).ToDictionary(static id => (long)id, id => new OptionOiCacheEntry(id, now)), now);
            Check(OptionOpenInterestCache.LoadSnapshot(dir, "QQQ", expiration, now).Values.Count == OptionOpenInterestCache.MaximumEntries,
                "Writer and reader respect entry bound");
        }
        finally { Clean(dir); }
    }

    public static void TradingSegments()
    {
        var ranges = IbTradingHoursParser.ParseEastern("20260908:0930-1200,1300-1600;20260909:CLOSED", true);
        Check(ranges.Count == 2 && ranges[0].EndUtc == Et(9, 8, 12) && ranges[1].StartUtc == Et(9, 8, 13), "Split ranges preserve maintenance gap");
        Check(IbTradingHoursParser.Parse("20260908:0930-1600", "bad/timezone", true).Count == 0, "Unknown timezone does not silently become ET");
        Check(IbTradingHoursParser.ParseEastern("20260308:0230-0400", true).Count == 0, "Nonexistent DST wall time rejected");
        Check(IbTradingHoursParser.ParseEastern("20261101:0130-0300", true).Count == 0, "Ambiguous DST wall time rejected");
        Check(IbTradingHoursParser.ParseEastern("20260907:2000-20260908:1600", false).Single().StartUtc == Et(9, 8, 9, 30), "Overnight hours intersect next-day RTH");
        Check(IbTradingHoursParser.ParseEastern("20261127:0400-2000", false).Single().EndUtc == Et(11, 27, 13), "Half day clipped");
        Check(IbTradingHoursParser.ParseEastern("20260907:0930-1600", false).Count == 0, "NYSE holiday not RTH");
        Check(IbTradingHoursParser.ParseEastern("20260908:2000-2400", true).Single().EndUtc == Et(9, 9, 0), "Midnight endpoint");
        var overlap = IbTradingHoursParser.ParseEastern("20260908:0930-1100;20260908:1000-1200;20260908:1200-1300", true);
        Check(overlap.Count == 2 && overlap[0].EndUtc == Et(9, 8, 12), "Only overlaps merged, adjacent anchors retained");
    }

    public static void FixedBaselines()
    {
        var segmentStart = Et(9, 8, 13);
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var samples = new[] { new OptionCumulativeSample(1, Et(9, 8, 11, 59), 10, 2, 100),
                new OptionCumulativeSample(1, segmentStart.AddSeconds(40), 110, 2, 100, 2, 1) };
            Check(OptionFlowAggregation.TryCalculateBucketValue(samples, segmentStart, segmentStart.AddMinutes(minutes), true, out var value, segmentStart)
                && value.Volume == 1 && value.IsPartial, "Never attribute maintenance-gap cumulative delta to new bucket");
            Check(!OptionFlowAggregation.TryCalculateBucketValue(samples, segmentStart.AddMinutes(minutes), segmentStart.AddMinutes(minutes * 2), true, out _, segmentStart), "No events stays unknown");
            var segment = new OptionTradingSegment(segmentStart, segmentStart.AddMinutes(minutes).AddSeconds(20));
            Check(OptionFlowAggregation.GetPreviousCompletedBucket(segment.EndUtc.AddSeconds(3), segment, minutes, TimeSpan.FromSeconds(3))?.EndUtc
                == segmentStart.AddMinutes(minutes), "Discard incomplete session tail");
        }
        var huge = new OptionCumulativeSample(1, segmentStart, long.MaxValue, decimal.MaxValue, 100);
        Check(!huge.HasValidCumulativeValue() && !OptionFlowAggregation.TryCalculateDelta(huge, huge, out _), "Overflowing cumulative value rejected");
        Check(!(huge with { LastTradePrice = decimal.MaxValue, LastTradeSize = long.MaxValue }).TryGetLastTradeValue(out _), "Overflowing single trade rejected");
    }

    public static void ActualOiAndTarget()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 5, "QQQ", true);
        var indicator = fixture.Indicator; var now = Et(9, 1, 11);
        Set(indicator, "_showOptionOpenInterest", true);
        var oi = (Dictionary<long, long>)Get(indicator, "_optionOpenInterest")!;
        var received = (Dictionary<long, DateTime>)Get(indicator, "_optionOiReceivedByContract")!;
        oi[1] = 0; received[1] = Et(8, 31, 10); Set(indicator, "_optionOpenInterestReceivedUtc", received[1]);
        fixture.PublishAt(now);
        var stale = Get(indicator, "_optionOpenInterestSnapshot")!;
        Check(Prop(stale, "Status")!.ToString() == "Frozen" && Prop(stale, "Message")!.ToString()!.Contains("旧值待确认"), "Stale OI retained but labeled");
        received[1] = now; Set(indicator, "_oiRevision", 10L); Set(indicator, "_optionOpenInterestReceivedUtc", now);
        fixture.PublishAt(now);
        Check(Prop(Get(indicator, "_optionOpenInterestSnapshot")!, "Status")!.ToString() == "Partial", "Confirmed subset has partial coverage");
        var updateType = assembly.GetType("WolfMoss.ATAS.PriceMapping.IbOptionMarketDataUpdate", true)!;
        var contract = ((Array)Get(indicator, "_activeOptionContracts")!).GetValue(0)!;
        var priorRevision = (long)Get(indicator, "_oiRevision")!;
        object Update(DateTime time, long value) => Activator.CreateInstance(updateType,
            contract, time, (long?)value, null, null, false, null, null)!;
        Call(indicator, "OnOptionMarketData", Update(now.AddSeconds(1), 0));
        Check((long)Get(indicator, "_oiRevision")! == priorRevision && received[1] == now,
            "Repeated same-day OI does not dirty cache or redraw");
        Call(indicator, "OnOptionMarketData", Update(now.AddSeconds(-1), 999));
        Check(oi[1] == 0, "Out-of-order OI cannot replace current value");
        var exceptionType = assembly.GetType("WolfMoss.ATAS.PriceMapping.IbOptionGatewayException", true)!;
        Call(indicator, "PublishOptionError", Activator.CreateInstance(exceptionType, "CONNECT_TIMEOUT", "test timeout", null));
        Check((DateTime)Prop(Get(indicator, "_optionOpenInterestSnapshot")!, "ReceivedUtc")! == now, "Failure does not rewrite OI data receipt");
        var dir = NewDirectory();
        try
        {
            Check((bool)Call(indicator, "TransitionOptionTarget", "SPX", new DateOnly(2026, 9, 2), now, dir)!, "New target activated before discovery");
            Call(indicator, "PublishOptionError", Activator.CreateInstance(exceptionType, "NO_CONTRACTS", "test missing contracts", null));
            foreach (var field in new[] { "_optionOpenInterestSnapshot", "_optionFlowSnapshot" })
            {
                var snapshot = Get(indicator, field)!;
                Check((string)Prop(snapshot, "Ticker")! == "SPX" && !((IEnumerable)Prop(snapshot, "Rows")!).Cast<object>().Any(), "Failed new target cannot display old rows");
            }
            Check(oi.Count == 0 && received.Count == 0, "Target change clears old per-contract state");
        }
        finally { Clean(dir); }
    }

    public static void ActualFixedReception()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 5, "QQQ", true);
        var indicator = fixture.Indicator;
        var contracts = (Array)Get(indicator, "_activeOptionContracts")!;
        var sampleType = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.OptionCumulativeSample", true)!;
        object Sample(long id, DateTime at, long volume = 100) => Activator.CreateInstance(sampleType, id, at, volume, 2m, 100m, 2m, (long?)1)!;
        var id = (long)Prop(contracts.GetValue(0)!, "ConId")!; var now = Et(9, 1, 11);
        Check((bool)Call(indicator, "AcceptFixedSample", Sample(id, now), now)!, "Active in-session sample accepted");
        Check(!(bool)Call(indicator, "AcceptFixedSample", Sample(99999, now), now)!, "Retired contract rejected");
        Check(!(bool)Call(indicator, "AcceptFixedSample", Sample(id, now.AddSeconds(1)), now)!, "Future sample rejected");
        Check(!(bool)Call(indicator, "AcceptFixedSample", Sample(id, Et(9, 1, 9)), now)!, "Premarket sample rejected");
        Call(indicator, "SuspendOptionObservation", now);
        Check(((IDictionary)Get(indicator, "_regularTradeSamples")!).Count == 0, "Disconnect clears old fixed baseline but not published frame");
        Check(!(bool)Call(indicator, "AcceptFixedSample", Sample(id, now.AddSeconds(-1)), now)!, "Pre-gap callback rejected");
        var add = indicator.GetType().GetMethod("AddCumulativeSample", BindingFlags.Static | BindingFlags.NonPublic)!;
        var source = (IDictionary)Get(indicator, "_regularTradeSamples")!;
        add.Invoke(null, new[] { source, Sample(id, now) });
        add.Invoke(null, new[] { source, Sample(id, now.AddSeconds(-1), 50) });
        Check(((IList)source[id]!).Count == 1 && (long)Prop(((IList)source[id]!)[0]!, "TotalVolume")! == 100, "Out-of-order event cannot erase baseline");
    }
}
