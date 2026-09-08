using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using WolfMoss.ATAS.PriceMapping.Core;

// Explicit offline microbenchmark, NOT an ATAS FPS / IB throughput measurement.
// Uses the built Pro's real CreateFlowSnapshot path. Fixed inputs include the current
// per-publish list-copy pattern so the known two-hour allocation cost remains visible.
internal static class PerformanceBaseline
{
    private static readonly DateTime Now = new(2026, 9, 1, 15, 0, 4, DateTimeKind.Utc);
    public static void Run(bool publication = false)
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        Console.WriteLine($"build_id={assembly.ManifestModule.ModuleVersionId:N}; seed=2210; runtime={Environment.Version}; os={Environment.OSVersion}");
        if (publication) Console.WriteLine("harness=actual_publish; oi=false; flow=true; clock=fixed; warmup=3; samples=20");
        Console.WriteLine("mode,minutes,contracts,consumers,mean_ms_per_refresh,p95_ms_per_refresh,allocated_bytes_per_refresh,input_events");
        foreach (var mode in new[] { OptionFlowBucketMode.PreviousCompletedFixed, OptionFlowBucketMode.Rolling })
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        foreach (var targetCount in new[] { 1, 2 })
        foreach (var consumers in new[] { 1, 2, 8 })
        {
            var fixtures = Enumerable.Range(0, targetCount * consumers)
                .Select(i => new Fixture(assembly, mode, minutes, i % targetCount == 0 ? "QQQ" : "SPX", publication)).ToArray();
            for (var i = 0; i < 3; i++) foreach (var fixture in fixtures) fixture.Refresh();
            var timings = new double[20];
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < timings.Length; i++)
            {
                var start = Stopwatch.GetTimestamp();
                foreach (var fixture in fixtures) fixture.Refresh();
                timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            var allocated = (GC.GetAllocatedBytesForCurrentThread() - beforeBytes) / timings.Length;
            Array.Sort(timings);
            Console.WriteLine(FormattableString.Invariant($"{mode},{minutes},{targetCount * 42},{consumers},{timings.Average():F4},{timings[18]:F4},{allocated},{fixtures.Sum(f => f.EventCount)}"));
        }
        // Deterministic quality scenarios are asserted independently of machine performance.
        ReplayQualityScenarios();
    }

    public static void ReplayQualityScenarios()
    {
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var start = Now.AddMinutes(-minutes);
            var contract = new OptionContractDescriptor(1, "SPX", DateOnly.FromDateTime(Now), 7700,
                OptionRight.Call, "SPXW", "SMART", 100, "");
            var book = new OptionRollingFlowState();
            book.SetTradingSegments(new[] { new OptionTradingSegment(start.AddHours(-1), Now.AddHours(1)) }, start);
            book.ConfigureLadder(new[] { contract }, 7700, start, minutes);
            var empty = book.CreateSnapshot("SPX", contract.Expiration, start.AddSeconds(1), minutes, OptionFlowTradeScope.RegularTrades, 21, false);
            if (empty.Rows[0].CallPremium != null) throw new InvalidOperationException("Idle must not invent trades");
            var time = start.AddSeconds(10);
            long volume = 100;
            // Burst, disconnect and re-entry are seeded and deterministic; no live resources.
            for (var i = 0; i < 100; i++)
                book.Add(new(1, time.AddMilliseconds(i), volume++, 2m, 100m, 2m, 1), OptionFlowTradeScope.RegularTrades, time.AddMilliseconds(i));
            book.Suspend(start.AddSeconds(20));
            book.Resume(start.AddSeconds(30));
            book.Add(new(1, start.AddSeconds(31), 10000, 2m, 100m, 2m, 1), OptionFlowTradeScope.RegularTrades, start.AddSeconds(31));
            book.ConfigureLadder(Array.Empty<OptionContractDescriptor>(), 7705, start.AddSeconds(40), minutes);
            var retained = book.CreateSnapshot("SPX", contract.Expiration, Now, minutes, OptionFlowTradeScope.RegularTrades, 21, false);
            if (retained.Rows.Count != 1 || retained.Rows[0].CallVolume != 101 || !retained.Rows[0].CallFlowIsPartial)
                throw new InvalidOperationException("Gap/retention must preserve observed partial volume only");
        }
        Console.WriteLine("quality_scenarios=PASS (idle, burst, disconnect, range retirement; all intervals)");
    }

    internal sealed class Fixture
    {
        private readonly object _indicator;
        private readonly Type _indicatorType;
        private readonly MethodInfo _create;
        private readonly Type _sampleList;
        private readonly Type _sampleDictionary;
        private readonly IDictionary _samples;
        private readonly Array _contracts;
        private readonly string _ticker;
        private readonly decimal[] _strikes;
        private readonly FieldInfo _snapshot;
        private readonly MethodInfo? _publish;
        public long EventCount { get; }
        internal object Indicator => _indicator;
        internal void PublishAt(DateTime utc) => _publish!.Invoke(_indicator, new object[] { utc });
        public Fixture(Assembly assembly, OptionFlowBucketMode mode, int minutes, string ticker, bool publication,
            string tradingHours = "20260901:0930-20260901:1600")
        {
            _ticker = ticker;
            Type Core(string name) => assembly.GetType("WolfMoss.ATAS.PriceMapping.Core." + name, true)!;
            _indicatorType = assembly.GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!;
            _indicator = RuntimeHelpers.GetUninitializedObject(_indicatorType);
            void Set(string name, object value) => _indicatorType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_indicator, value);
            _create = _indicatorType.GetMethod("CreateFlowSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            if (publication) _publish = _indicatorType.GetMethod("PublishOptionSnapshotsAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _snapshot = _indicatorType.GetField("_optionFlowSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var bucketMode = Enum.ToObject(Core("OptionFlowBucketMode"), (int)mode);
            var scope = Enum.ToObject(Core("OptionFlowTradeScope"), 0);
            Set("_optionDataSync", new object());
            Set("_optionFlowBucketMode", bucketMode); Set("_optionFlowTradeScope", scope);
            Set("_optionFlowIntervalMinutes", minutes); Set("_optionStrikeLevels", 21);
            Set("_optionFlowSnapshot", Core("OptionFlowSnapshot").GetMethod("Disabled")!.Invoke(null, new object[] { Now, bucketMode, scope, minutes })!);
            var book = Activator.CreateInstance(Core("OptionRollingFlowState"))!;
            Set("_rollingFlow", book);
            var segmentType = Core("OptionTradingSegment");
            var segments = Array.CreateInstance(segmentType, 1);
            segments.SetValue(Activator.CreateInstance(segmentType, Now.AddHours(-3), Now.AddHours(1)), 0);
            book.GetType().GetMethod("SetTradingSegments")!.Invoke(book, new object[] { segments, Now.AddMinutes(-minutes) });
            var contractType = Core("OptionContractDescriptor");
            _contracts = Array.CreateInstance(contractType, 42);
            _strikes = Enumerable.Range(0, 21).Select(i => ticker == "QQQ" ? 700m + i : 7650m + 5 * i).ToArray();
            var expiration = DateOnly.FromDateTime(Now);
            for (var i = 0; i < 42; i++)
                _contracts.SetValue(Activator.CreateInstance(contractType, (long)i + 1, ticker, expiration,
                    _strikes[i / 2], Enum.ToObject(Core("OptionRight"), i % 2), ticker == "QQQ" ? "QQQ" : "SPXW",
                    "SMART", 100m, tradingHours, "America/New_York"), i);
            book.GetType().GetMethod("ConfigureLadder")!.Invoke(book, new object[] { _contracts, _strikes[10], Now.AddMinutes(-minutes), minutes });
            _sampleList = typeof(List<>).MakeGenericType(Core("OptionCumulativeSample"));
            _sampleDictionary = typeof(Dictionary<,>).MakeGenericType(typeof(long), _sampleList);
            _samples = (IDictionary)Activator.CreateInstance(_sampleDictionary)!;
            var sampleType = Core("OptionCumulativeSample");
            var add = book.GetType().GetMethod("Add")!;
            var random = new Random(2210);
            var horizon = mode == OptionFlowBucketMode.Rolling ? minutes * 60 : 7200;
            // Samples have increasing source time and cumulative values. All contracts get one event / 5s.
            for (var second = horizon - 1; second >= 1; second -= 5)
            for (var i = 0; i < 42; i++)
            {
                var time = Now.AddSeconds(-second);
                var volume = (horizon - second) * 10L + random.Next(1, 5);
                var sample = Activator.CreateInstance(sampleType, (long)i + 1, time, volume, 2m, 100m, 2m, 1L)!;
                if (mode == OptionFlowBucketMode.Rolling)
                    add.Invoke(book, new object[] { sample, scope, time });
                else
                {
                    if (!_samples.Contains((long)i + 1)) _samples.Add((long)i + 1, Activator.CreateInstance(_sampleList));
                    ((IList)_samples[(long)i + 1]!).Add(sample);
                }
                EventCount++;
            }
            Set("_performance", Activator.CreateInstance(Core("PerformanceCollector"))!);
            Set("_showOptionPremiumFlow", true);
            Set("_activeOptionContracts", _contracts); Set("_activeOptionStrikes", _strikes);
            Set("_activeOptionTicker", ticker); Set("_activeOptionExpiration", expiration);
            Set("_activeAtmStrike", _strikes[10]); Set("_flowCoverageStartUtc", Now.AddHours(-2));
            Set("_optionOpenInterest", new Dictionary<long, long>());
            Set("_optionOiReceivedByContract", new Dictionary<long, DateTime>());
            Set("_regularTradeSamples", _samples);
            Set("_allTradeSamples", Activator.CreateInstance(_sampleDictionary)!);
        }
        public void Refresh()
        {
            if (_publish != null) { _publish.Invoke(_indicator, new object[] { Now }); return; }
            var copied = (IDictionary)Activator.CreateInstance(_sampleDictionary)!;
            foreach (DictionaryEntry entry in _samples)
                copied.Add(entry.Key, Activator.CreateInstance(_sampleList, new[] { entry.Value }));
            var snapshot = _create.Invoke(_indicator, new object[] { _ticker, DateOnly.FromDateTime(Now), _strikes[10],
                _strikes, _contracts, copied, Now, Now.AddHours(-2), false })!;
            _snapshot.SetValue(_indicator, snapshot);
        }
    }
}
