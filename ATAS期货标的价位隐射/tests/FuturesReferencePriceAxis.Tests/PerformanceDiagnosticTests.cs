using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class PerformanceDiagnosticTests
{
    private static readonly DateTime Utc = new(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static void WindowAndOff()
    {
        var window = new PerformanceDurationWindow();
        Parallel.For(0, 10000, _ => window.Record(Stopwatch.Frequency / 1000, Utc));
        var result = window.Read(Utc);
        Check(result.Count == 10000 && Math.Abs(result.MeanMs - 1) < .001, "Concurrent sample accounting");
        Check(result.P95UpperMs >= 1 && result.P95UpperMs < 2, "Histogram percentile bound");
        Check(window.Read(Utc.AddSeconds(60)).Count == 0, "60-second expiration");
        window.Record(Stopwatch.Frequency, Utc.AddSeconds(60));
        window.Record(Stopwatch.Frequency, Utc); // stale producer cannot replace current slot
        Check(window.Read(Utc.AddSeconds(60)).Count == 1, "Stale producer slot protection");
        var collector = new PerformanceCollector();
        using (collector.Measure(PerformanceMetric.Render)) collector.EventReceived();
        Check(collector.Sample(Utc, null, 84).Render.Count == 0, "Off must not measure");
        collector.Enabled = true;
        using (collector.Measure(PerformanceMetric.Render)) Thread.SpinWait(20);
        collector.EventReceived();
        var enabled = collector.Sample(DateTime.UtcNow, null, 84);
        Check(enabled.Render.Count == 1 && enabled.EventQueueLength == null && enabled.MarketDataGaps == null,
            "Enabled timing and unavailable metrics");
        var before = GC.GetAllocatedBytesForCurrentThread();
        collector.Enabled = false;
        for (var i = 0; i < 10000; i++) using (collector.Measure(PerformanceMetric.Render)) { }
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "Off scope must allocate nothing");
    }

    public static void RatesAndTasks()
    {
        var collector = new PerformanceCollector { Enabled = true };
        collector.Sample(Utc, new(1, 100, 10, 42, 2, 1), 84);
        collector.EventReceived(); collector.EventReceived();
        collector.RecordError("untrusted content should not escape");
        using (collector.TrackTask())
        {
            var s = collector.Sample(Utc.AddSeconds(2), new(1, 110, 12, 42, 2, 1), 84);
            Check(s.EventsPerSecond == 1 && s.SendsPerSecond == 5 && s.CancelsPerSecond == 1, "Rate deltas");
            Check(s.BackgroundTasks == 2 && s.Consumers == 2, "Task and shared consumer gauge");
            Check(s.LastErrorCode == "OTHER", "Error allowlist");
        }
        var replaced = collector.Sample(Utc.AddSeconds(3), new(2, 1, 0, 1, 1, 1), 84);
        Check(replaced.GatewayReplacements == 1 && replaced.SendsPerSecond == 0 && replaced.BackgroundTasks == 1,
            "Gateway reset must not produce negative/spurious rates");
    }

    public static void RecorderLifecycle() => TestRecorderAsync().GetAwaiter().GetResult();
    private static async Task TestRecorderAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atas-perf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var metadata = new PerformanceRunMetadata("test-build", "Rolling", 5, 21, 84, true, true);
            var snapshot = new PerformanceCollector().Sample(Utc, null, 84);
            var gate = (SemaphoreSlim)typeof(PerformanceDiagnosticRecorder).GetField("FileGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            PerformanceDiagnosticRecorder saturated;
            gate.Wait();
            try
            {
                saturated = new PerformanceDiagnosticRecorder(dir, metadata);
                for (var i = 0; i < 120; i++) Check(saturated.TryRecord(snapshot), "Bounded queue capacity");
                Check(!saturated.TryRecord(snapshot) && saturated.Dropped == 1, "Overflow is visible and non-blocking");
            }
            finally { gate.Release(); }
            await saturated.DisposeAsync();
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            var recorders = Enumerable.Range(0, 3).Select(_ => new PerformanceDiagnosticRecorder(dir, metadata)).ToArray();
            foreach (var recorder in recorders)
            {
                Check(recorder.TryRecord(snapshot), "Recorder accepts summary");
                await recorder.DisposeAsync();
                await recorder.DisposeAsync();
                Check(recorder.State == "STOPPED", "Completion flushes without next sample");
                Check(!recorder.TryRecord(snapshot), "Reject after completion");
            }
            Check(Directory.GetFiles(dir, "*.csv").Length == 3, "Independent run names");
            foreach (var file in Directory.GetFiles(dir, "*.csv"))
            {
                var lines = File.ReadAllLines(file);
                Check(lines.Length == 2 && lines[0].Split(',').Length == lines[1].Split(',').Length,
                    "CSV shape / graceful flush");
                Check(lines[1].EndsWith(",,"), "Unknown metrics stay empty, never false zero");
            }
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                Check(json.RootElement.GetProperty("build_id").GetString() == "test-build", "Snake case metadata");
                Check(!File.ReadAllText(file).Contains("host", StringComparison.OrdinalIgnoreCase), "Allowlist only metadata");
            }
            var invalid = Path.Combine(dir, "not-a-directory");
            File.WriteAllText(invalid, "fixture");
            var failed = new PerformanceDiagnosticRecorder(invalid, metadata);
            await failed.DisposeAsync();
            Check(failed.State == "WRITE_ERROR" && !failed.TryRecord(snapshot), "Filesystem failure isolated");

            var foreign = Path.Combine(dir, "perf-user-notes.csv");
            File.WriteAllText(foreign, "keep");
            foreach (var file in Directory.GetFiles(dir)) File.SetLastWriteTimeUtc(file, Utc.AddDays(-8));
            PerformanceDiagnosticRecorder.Prune(dir, Utc);
            Check(File.Exists(foreign) && File.Exists(invalid), "Never delete unrelated files");
            Check(Directory.GetFiles(dir, "*.json").Length == 0, "Retention deletes expired owned files");
            var huge = Path.Combine(dir, "perf-20260901T140000-" + Guid.NewGuid().ToString("N") + "-0000.csv");
            using (var stream = File.Create(huge)) stream.SetLength(PerformanceDiagnosticRecorder.DirectoryLimit + 1);
            PerformanceDiagnosticRecorder.Prune(dir, DateTime.UtcNow);
            Check(!File.Exists(huge), "Directory capacity enforced on owned files");
        }
        finally
        {
            // A test-created unique directory only; no recursive deletion of a user-configured path.
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }

    public static Assembly LoadPro()
    {
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(@"C:\Program Files (x86)\ATAS Platform", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        // A test process retains the resolver for fixtures' deferred ATAS type loading; no network.
        AssemblyLoadContext.Default.Resolving -= Resolve;
        AssemblyLoadContext.Default.Resolving += Resolve;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "src",
            "FuturesReferencePriceAxis.DealerHeatmap", "bin", configuration, "FuturesReferencePriceAxis.DealerHeatmap.dll"));
    }

    public static void ActualSettingAndCallback()
    {
        var assembly = LoadPro();
        var type = assembly.GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!;
        var indicator = RuntimeHelpers.GetUninitializedObject(type);
        var property = type.GetProperty("PerformanceDiagnosticsMode")!;
        Check(property.GetValue(indicator)!.ToString() == "Off", "Diagnostic default compatibility");
        var core = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.PerformanceCollector", true)!;
        var collector = Activator.CreateInstance(core)!;
        core.GetProperty("Enabled")!.SetValue(collector, true);
        void Set(string name, object value) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(indicator, value);
        Set("_performance", collector);
        Set("_optionDataSync", new object());
        var updateType = assembly.GetType("WolfMoss.ATAS.PriceMapping.IbOptionMarketDataUpdate", true)!;
        // Default update is rejected by target guard, but its processing time must still be counted.
        var callback = type.GetMethod("OnOptionMarketData", BindingFlags.Instance | BindingFlags.NonPublic)!;
        callback.Invoke(indicator, new[] { Activator.CreateInstance(updateType) });
        Type Core(string name) => assembly.GetType("WolfMoss.ATAS.PriceMapping.Core." + name, true)!;
        Set("_activeOptionContracts", Array.CreateInstance(Core("OptionContractDescriptor"), 0));
        Set("_activeOptionStrikes", Array.Empty<decimal>());
        Set("_optionOpenInterest", new Dictionary<long, long>());
        Set("_optionOiReceivedByContract", new Dictionary<long, DateTime>());
        var sampleList = typeof(List<>).MakeGenericType(Core("OptionCumulativeSample"));
        var sampleDictionary = typeof(Dictionary<,>).MakeGenericType(typeof(long), sampleList);
        Set("_regularTradeSamples", Activator.CreateInstance(sampleDictionary)!);
        Set("_allTradeSamples", Activator.CreateInstance(sampleDictionary)!);
        Set("_rollingFlow", Activator.CreateInstance(Core("OptionRollingFlowState"))!);
        type.GetMethod("PublishOptionSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(indicator, null);
        var render = type.GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
        render.Invoke(indicator, new object?[] { null, Enum.ToObject(render.GetParameters()[1].ParameterType, 0) });
        var sample = core.GetMethod("Sample")!.Invoke(collector, new object?[] { DateTime.UtcNow, null, 84, 0L, "SUMMARY" })!;
        var timing = sample.GetType().GetProperty("Callback")!.GetValue(sample)!;
        Check((long)timing.GetType().GetProperty("Count")!.GetValue(timing)! == 1, "Actual indicator callback instrumentation");
        foreach (var metric in new[] { "Publish", "Render" })
        {
            var metricValue = sample.GetType().GetProperty(metric)!.GetValue(sample)!;
            Check((long)metricValue.GetType().GetProperty("Count")!.GetValue(metricValue)! == 1,
                "Actual indicator " + metric + " instrumentation");
        }
        var gateway = assembly.GetType("WolfMoss.ATAS.PriceMapping.ReflectionIbOptionGatewayClient", true)!;
        var getter = gateway.GetProperty("PerformanceSnapshot")!.GetMethod!;
        Check(getter.GetMethodBody() != null, "Actual gateway diagnostics entry exists");
        Set("_performanceLifecycleSync", new object());
        Set("_performanceTask", Task.CompletedTask);
        Set("_performanceLines", Array.Empty<string>());
        type.BaseType!.GetField("_initialized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(indicator, true);
        for (var i = 0; i < 30; i++)
        {
            try
            {
                property.SetValue(indicator, Enum.ToObject(property.PropertyType, 1));
                property.SetValue(indicator, Enum.ToObject(property.PropertyType, 0));
            }
            catch (TargetInvocationException exception)
            { throw new InvalidOperationException(exception.InnerException?.ToString()); }
        }
        var task = (Task)type.GetField("_performanceTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(indicator)!;
        task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(!(bool)core.GetProperty("Enabled")!.GetValue(collector)!, "Rapid Off switch leaves no collector running");
        Check(((string[])type.GetField("_performanceLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(indicator)!).Length == 0,
            "Rapid switch leaves no stale diagnostics");
    }

    public static void RecorderTimedFlushAndRotation() => TestTimedFlushAsync().GetAwaiter().GetResult();
    private static async Task TestTimedFlushAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atas-perf-timed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var recorder = new PerformanceDiagnosticRecorder(dir, new("test", "Rolling", 1, 21, 84, false, true));
            try
            {
                recorder.TryRecord(new PerformanceCollector().Sample(Utc, null, 84));
                var deadline = DateTime.UtcNow.AddSeconds(14);
                while (Directory.GetFiles(dir, "*.csv").Length == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                Check(Directory.GetFiles(dir, "*.csv").Length == 1, "Flush at 10s without a next sample");
                var file = Directory.GetFiles(dir, "*.csv")[0];
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
                    stream.SetLength(PerformanceDiagnosticRecorder.FileLimit);
                recorder.TryRecord(new PerformanceCollector().Sample(Utc, null, 84));
            }
            finally { await recorder.DisposeAsync(); }
            Check(recorder.State == "STOPPED" && Directory.GetFiles(dir, "*.csv").Length == 2, "Rotate at 10MiB");
        }
        finally
        {
            foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
            Directory.Delete(dir);
        }
    }
}
