using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class StabilityBatchOneTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 15, 0, 4, DateTimeKind.Utc);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static object Get(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static object? Prop(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner);

    internal sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Utc = utc;
        public long Timestamp;
        public override DateTimeOffset GetUtcNow() => new(Utc);
        public override long GetTimestamp() => Timestamp;
        public override long TimestampFrequency => 1000;
    }

    public static void ClockDiscontinuities()
    {
        var guard = new RealtimeClockGuard();
        Check(!guard.Observe(Now, 0, 1000), "First sample is not a clock jump");
        Check(!guard.Observe(Now.AddMinutes(30), 1800000, 1000), "A stalled loop is not a wall-clock adjustment");
        Check(guard.Observe(Now.AddMinutes(34), 1801000, 1000) && guard.LastAdjustmentSeconds == 239,
            "Detect forward correction against monotonic time");
        Check(guard.Observe(Now.AddMinutes(30), 1802000, 1000) && guard.LastAdjustmentSeconds == -241,
            "Detect backward correction");
        Check(!guard.Observe(Now.AddMinutes(30).AddSeconds(1), 1803000, 1000) && guard.JumpCount == 2,
            "No repeated resets after stabilization");
    }

    public static void ActualClockAndFutureCache()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        foreach (var ticker in new[] { "QQQ", "SPX" })
        foreach (var minutes in new[] { 1, 3, 5, 10 })
        {
            var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, minutes, ticker, true);
            var indicator = fixture.Indicator;
            Set(indicator, "_realtimeClock", new Clock(Now));
            Call(indicator, "PublishOptionSnapshots");
            var current = Get(indicator, "_optionFlowSnapshot");
            Check((DateTime)Prop(current, "BucketEndUtc")! <= Now && (DateTime)Prop(current, "ReceivedUtc")! == Now,
                "Production publication uses the injected live UTC clock, not host time");
            Check(((IEnumerable)Prop(current, "Rows")!).Cast<object>().Any(row => Prop(row, "CallPremium") != null),
                "Current-source events produce values at the correct clock");
            var earlier = Now.AddMinutes(-minutes);
            fixture.PublishAt(earlier); // Also protect direct publication from a cached future frame.
            Check((DateTime)Prop(Get(indicator, "_optionFlowSnapshot"), "BucketEndUtc")! <= earlier,
                "Future cached completed frame must not survive rollback");
        }
    }

    public static void ActualClockRebase()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        foreach (var mode in new[] { OptionFlowBucketMode.PreviousCompletedFixed, OptionFlowBucketMode.Rolling })
        {
            var fixture = new PerformanceBaseline.Fixture(assembly, mode, 1, "QQQ", true);
            var indicator = fixture.Indicator;
            var clock = new Clock(Now);
            Set(indicator, "_realtimeClock", clock);
            Call(indicator, "PublishOptionSnapshots");
            clock.Utc = Now.AddMinutes(-3); clock.Timestamp = 1000;
            Call(indicator, "PublishOptionSnapshots");
            Check((long)Prop(Get(indicator, "_flowClockGuard"), "JumpCount")! == 1, "Actual publisher detects correction");
            Check((DateTime)Get(indicator, "_flowCoverageStartUtc") == clock.Utc, "Fresh baseline after correction");
            Check(((IDictionary)Get(indicator, "_regularTradeSamples")).Count == 0, "No cross-clock cumulative subtraction");
            var frame = Get(indicator, "_optionFlowSnapshot");
            Check(Prop(frame, "BucketEndUtc") is not DateTime end || end <= clock.Utc, "Rolling cannot retain future internal clock");
            Check(!((IEnumerable)Prop(frame, "Rows")!).Cast<object>().Any(row => Prop(row, "CallPremium") != null),
                "No fabricated premium from cleared baselines");
            clock.Utc = clock.Utc.AddSeconds(1); clock.Timestamp += 1000;
            Call(indicator, "PublishOptionSnapshots");
            Check((long)Prop(Get(indicator, "_flowClockGuard"), "JumpCount")! == 1, "Stable clock does not repeatedly erase samples");
        }
    }

    public static void DiagnosticReasonsAndCsv()
    {
        Check(FlowReceptionDiagnostics.DescribeDisposition("RollingBoundary") == "观察边界或共享去重",
            "UI must not mislabel shared deduplication as a rolling-only rejection");
        Check(FlowReceptionDiagnostics.DescribeDisposition("FutureSource") == "源时间在未来",
            "Real source-time rejection remains distinct from deduplication");
        var diagnostics = new FlowReceptionDiagnostics();
        foreach (var disposition in Enum.GetValues<FlowSampleDisposition>())
            diagnostics.Record(Now.AddSeconds(-5), Now, disposition, Now.AddSeconds(-2));
        var reception = diagnostics.Read();
        Check(reception.Accepted == 1 && reception.Rejected == 9 && reception.Duplicates == 1 && reception.Buffered == 1 && reception.Late == 12,
            "Acceptance, rejection and late delivery are distinct counters");
        _ = MeasureReceptionAllocations(diagnostics);
        var allocated = MeasureReceptionAllocations(diagnostics);
        Check(allocated == 0, $"Diagnostic event recording allocates nothing (actual: {allocated})");
        var collector = new PerformanceCollector { Enabled = true };
        collector.RecordError("LINE_LIMIT", "LOCAL_BUDGET");
        var local = collector.Sample(Now, null, 84);
        Check(local.LastErrorOrigin == "LOCAL_BUDGET" && local.LastRawErrorCode == null, "Local budget is not IB 101");
        collector.RecordError("LINE_LIMIT", "IB", 101);
        var snapshot = collector.Sample(Now, null, 84) with
        { AtasUtc = Now.AddMinutes(4), Reception = reception, BucketEndUtc = Now, ClockJumps = 2 };
        var header = PerformanceDiagnosticRecorder.Header.Split(',');
        var row = PerformanceDiagnosticRecorder.ToCsv(snapshot).Split(',');
        Check(header.Length == row.Length, "Diagnostic CSV schema matches output");
        var columns = header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second);
        Check(columns["atas_offset_seconds"] == "240" && columns["last_raw_error_code"] == "101"
            && columns["last_error_origin"] == "IB" && columns["flow_late"] == "12"
            && columns["flow_duplicates"] == "1" && columns["flow_observation_gaps"] == "1", "Clock and origin evidence survives recording");
        collector.RecordError("sensitive-placeholder", "sensitive-placeholder", -1);
        var safe = PerformanceDiagnosticRecorder.ToCsv(collector.Sample(Now, null, 84));
        Check(!safe.Contains("sensitive-placeholder"), "Only allowlisted classifications are recorded");
        collector.Enabled = false;
        collector.RecordError("LINE_LIMIT", "IB", 101);
        Check(collector.Sample(Now, null, 84).LastErrorCode == "OTHER", "Off does not collect errors");
    }

    public static void FutureRejectionEvidence()
    {
        var diagnostics = new FlowReceptionDiagnostics();
        var empty = diagnostics.Read();
        Check(empty.LastFutureSourceUtc == null && empty.LastFutureReceivedUtc == null
            && empty.LastFutureLeadMilliseconds == null && empty.MaxFutureLeadMilliseconds == null,
            "No future rejection is unknown, not zero");
        Check(FlowReceptionDiagnostics.DescribeFutureTimes(empty).Contains("暂无")
            && FlowReceptionDiagnostics.DescribeFutureLead(empty).Contains("--"), "Empty diagnostic labels");
        var received = new DateTime(2026, 9, 10, 23, 59, 59, 900, DateTimeKind.Utc);
        var source = received.AddTicks(2501250); // 250.125 ms; crosses UTC midnight.
        diagnostics.Record(source, received, FlowSampleDisposition.FutureSource, null);
        var first = diagnostics.Read();
        Check(first.LastFutureSourceUtc == source && first.LastFutureReceivedUtc == received
            && first.LastFutureLeadMilliseconds == 250.125 && first.MaxFutureLeadMilliseconds == 250.125,
            "Exact rejected timestamps and lead survive capture");
        diagnostics.Record(received.AddSeconds(-1), received.AddSeconds(1), FlowSampleDisposition.Accepted, null);
        diagnostics.Record(received, received.AddSeconds(1), FlowSampleDisposition.Duplicate, null);
        diagnostics.Record(received, received.AddSeconds(1), FlowSampleDisposition.OutsideLadder, null);
        var afterSuccess = diagnostics.Read();
        Check(afterSuccess.SourceUtc == received && afterSuccess.LastFutureSourceUtc == source
            && afterSuccess.LastFutureReceivedUtc == received && afterSuccess.LastFutureLeadMilliseconds == 250.125,
            "Ordinary latest callback and latest rejected callback are independent");
        diagnostics.Record(received.AddTicks(1), received, FlowSampleDisposition.FutureSource, null);
        var next = diagnostics.Read();
        Check(next.LastFutureLeadMilliseconds == 0.0001 && next.MaxFutureLeadMilliseconds == 250.125
            && first.LastFutureSourceUtc == source && first.LastFutureLeadMilliseconds == 250.125,
            "Smaller lead updates latest, preserves maximum and prior immutable snapshot");

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            Check(FlowReceptionDiagnostics.DescribeFutureTimes(first).Contains("09-11 00:00:00.150")
                && FlowReceptionDiagnostics.DescribeFutureTimes(first).Contains("09-10 23:59:59.900"),
                "UI shows milliseconds and dates across midnight");
            Check(FlowReceptionDiagnostics.DescribeFutureLead(next).Contains("0.0001")
                && FlowReceptionDiagnostics.DescribeFutureLead(next).Contains("250.1250"), "Invariant UI lead units");
            var snapshot = new PerformanceCollector().Sample(Now, null, 84) with { Reception = next };
            var header = PerformanceDiagnosticRecorder.Header.Split(',');
            var row = PerformanceDiagnosticRecorder.ToCsv(snapshot).Split(',');
            Check(header.Length == row.Length && header.Length == 63, "Append-only CSV schema 5");
            var columns = header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second);
            Check(columns["last_future_lead_ms"] == "0.0001" && columns["max_future_lead_ms"] == "250.125"
                && columns["last_future_source_utc"] == received.AddTicks(1).ToString("O")
                && columns["last_future_received_utc"] == received.ToString("O"), "CSV preserves sub-ms UTC evidence");
            Check(new PerformanceRunMetadata("test", "Rolling", 1, 21, 84, false, true).SchemaVersion == 5, "Metadata schema bumped");
            var emptyRow = PerformanceDiagnosticRecorder.ToCsv(snapshot with { Reception = null }).Split(',');
            Check(emptyRow.TakeLast(4).All(string.IsNullOrEmpty), "Missing evidence is blank in CSV");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        for (var i = 0; i < 10000; i++) diagnostics.Record(source, received, FlowSampleDisposition.FutureSource, null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) diagnostics.Record(source, received, FlowSampleDisposition.FutureSource, null);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Future diagnostics callback path allocates nothing");
    }

    public static void ActualReceptionReasons()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var fixture = new PerformanceBaseline.Fixture(assembly, OptionFlowBucketMode.PreviousCompletedFixed, 1, "QQQ", true);
        var indicator = fixture.Indicator;
        Get(indicator, "_performance").GetType().GetProperty("Enabled")!.SetValue(Get(indicator, "_performance"), true);
        ((IDictionary)Get(indicator, "_regularTradeSamples")).Clear();
        Set(indicator, "_flowCoverageStartUtc", Now.AddHours(-2));
        var sampleType = assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.OptionCumulativeSample", true)!;
        var scope = Enum.ToObject(assembly.GetType("WolfMoss.ATAS.PriceMapping.Core.OptionFlowTradeScope", true)!, 0);
        void Receive(long id, DateTime at, long volume = 100) => Call(indicator, "ReceiveFlowSample",
            Activator.CreateInstance(sampleType, id, at, volume, 2m, 100m, null, null), Now, scope);
        Receive(1, Now.AddSeconds(-10));
        Receive(1, Now.AddSeconds(-20)); // Out of order, not an empty bucket.
        Receive(1, Now.AddSeconds(1));
        Receive(1, Now.AddHours(-3));
        Receive(99999, Now);
        Receive(1, Now.AddMinutes(-100)); // Past coverage start but outside QQQ RTH.
        Receive(1, Now, -1);
        var result = Get(indicator, "_flowReception").GetType().GetMethod("Read")!.Invoke(Get(indicator, "_flowReception"), null)!;
        Check((long)Prop(result, "Accepted")! == 1 && (long)Prop(result, "Rejected")! == 6,
            "Actual production receiver reports every fixed rejection reason");
        foreach (var name in new[] { "Future", "BeforeCoverage", "OutsideLadder", "OutsideSession", "OutOfOrder", "Invalid" })
            Check((long)Prop(result, name)! == 1, "Reason: " + name);
    }

    // Isolate the allocation probe from tiered/OSR bookkeeping in the large test
    // method. This is an allocation assertion, not a CPU timing benchmark.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long MeasureReceptionAllocations(FlowReceptionDiagnostics diagnostics)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) diagnostics.Record(Now, Now, FlowSampleDisposition.Accepted, null);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    public static void CanceledWaitersAndLateFailure() => CanceledWaitersAsync().GetAwaiter().GetResult();

    public static void GatewayErrorOrigins() => GatewayErrorOriginsAsync().GetAwaiter().GetResult();
    private static async Task GatewayErrorOriginsAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        await using var cleanup = client;
        var contract = new OptionContractDescriptor(991, "QQQ", new(2026, 9, 1), 710,
            OptionRight.Call, "QQQ", "SMART", 100, "");
        IbOptionMarketDataUpdate? error = null;
        using var lease = await client.SubscribeAsync([contract], new(false, true, false), update => error = update, 1, default);
        try
        {
            await lease.UpdateAsync([contract, contract with { ConId = 992, Right = OptionRight.Put }], new(false, true, false), 1, default);
            throw new Exception("Expected local line rejection");
        }
        catch (IbOptionGatewayException rejected)
        { Check(rejected.Origin == "LOCAL_BUDGET" && rejected.RawErrorCode == null, "Actual local limiter origin"); }
        var id = socket.Operations.Single(op => op.Op == "request").Id;
        Call(client, "OnTickString", (object)new object[] { id, 77, "2;1;invalid;100;2;false" });
        Check(client.PerformanceSnapshot.InvalidRealtimeSamples == 1, "Malformed upstream timestamps counted before fanout");
        Call(client, "OnError", (object)new object[] { id, 0L, 101, "fixture line limit", "" });
        Check(error is { ErrorCode: "LINE_LIMIT", ErrorOrigin: "IB", RawErrorCode: 101 },
            "Actual IB 101 callback retains raw code and origin through fanout");
    }
    private static async Task CanceledWaitersAsync()
    {
        var requests = new SharedAsyncRequests<int, int>();
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var first = requests.GetAsync(1, () => source.Task, cancel.Token);
        var second = requests.GetAsync(1, () => throw new Exception("Must share"), cancel.Token);
        cancel.Cancel();
        foreach (var task in new[] { first, second })
        {
            try { await task; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        }
        source.SetException(new IOException("batch1-probe-late"));
        var result = 0;
        for (var i = 0; i < 50; i++)
        {
            try { result = await requests.GetAsync(1, () => Task.FromResult(7), default); break; }
            catch (IOException) { await Task.Delay(1); }
        }
        Check(result == 7, "Failed shared flight is evicted after every waiter canceled");
        var owned = IbTaskOwnership.Own(Task.FromException(new IOException("batch1-probe-await")));
        try { await owned; throw new Exception("Observation must not swallow await failure"); }
        catch (IOException) { }
    }

    public static void DisposalAndGc() => DisposalAndGcAsync().GetAwaiter().GetResult();
    private static async Task DisposalAndGcAsync()
    {
        // Explicitly bypass socket initialization: no Gateway, account or live API needed.
        var client = new ReflectionIbOptionGatewayClient(new("offline.invalid", 1, 9123));
        Set(client, "_initialization", Task.CompletedTask);
        var connecting = client.ConnectAsync(default);
        await client.DisposeAsync();
        try { await connecting; throw new Exception("Expected lifetime cancellation"); }
        catch (OperationCanceledException) { }
        Check(!client.ConnectionFaulted && ((TaskCompletionSource)Get(client, "_connected")).Task.IsCanceled,
            "Normal release is not CONNECT_TIMEOUT or a faulted signal");
        var unobserved = 0;
        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(error => error.Message.Contains("batch1-probe") || error.Message.Contains("IB Gateway 已释放")))
                Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Handler; // Test-only instrumentation, never installed by plugin.
        try
        {
            var references = CreateGcProbes();
            for (var i = 0; i < 10 && references.Any(reference => reference.IsAlive); i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
            Check(references.All(reference => !reference.IsAlive), "Fault probes were actually collected");
            Check(unobserved == 0, "No unobserved failures from released or abandoned operations");
        }
        finally { TaskScheduler.UnobservedTaskException -= Handler; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateGcProbes()
    {
        var released = new ReflectionIbOptionGatewayClient(new("offline.invalid", 1, 9124));
        released.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var connection = ((TaskCompletionSource)Get(released, "_connected")).Task;
        var signal = IbTaskOwnership.Completion<int>();
        signal.SetException(new IOException("batch1-probe-signal"));
        var aggregate = IbTaskOwnership.Own(Task.WhenAll(Task.FromException(new IOException("batch1-probe-aggregate"))));
        return [new(connection), new(signal.Task), new(aggregate)];
    }
}
