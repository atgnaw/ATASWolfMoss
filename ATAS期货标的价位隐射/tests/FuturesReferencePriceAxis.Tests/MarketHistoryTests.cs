using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using WolfMoss.MarketData;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class MarketHistoryTests
{
    private static readonly DateTime T = new(2026, 9, 1, 13, 30, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static MarketEvent Event(Guid run, long sequence) => new(1, run, sequence, MarketEventSource.Ib,
        MarketEventKind.OptionOpenInterest, Guid.Empty, 1, 10001, "QQQ", new(2026, 9, 1),
        new(1, "QQQ", new(2026, 9, 1), 700, MarketOptionRight.Call, "QQQ", "SMART", 100),
        null, T.AddSeconds(sequence), null, MarketTradeScope.NotApplicable,
        MarketEventQuality.SourceContinuityUnknown | MarketEventQuality.SourceTimeUnknown, OpenInterest: sequence);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    // Test-only bounded memory store. Production does not instantiate it or write files.
    internal sealed class MemoryStore : IMarketEventBatchWriter, IHistoricalDataReader
    {
        private readonly object _sync = new();
        private readonly List<MarketEvent> _events = new();
        private readonly Dictionary<Guid, MarketRecordingLoss> _losses = new();
        public MarketEvent[] Events { get { lock (_sync) return _events.ToArray(); } }
        public int Writes;
        public ValueTask WriteAsync(MarketRecordingBatch batch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_events.Count + batch.Events.Count > 4096) throw new IOException("Synthetic capacity");
                _events.AddRange(batch.Events); _losses[batch.RecordingRunId] = batch.Loss; Writes++;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<HistoricalReadResult> ReadAsync(HistoricalQuery query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); query.Validate();
            lock (_sync)
            {
                var offset = 0;
                if (query.ContinuationToken is { } cursor)
                {
                    var fields = cursor.Split(':');
                    if (fields.Length != 2 || !int.TryParse(fields[0], out var version) || version != Writes
                        || !int.TryParse(fields[1], out offset) || offset < 0)
                        return ValueTask.FromResult(new HistoricalReadResult(HistoricalReadStatus.Unavailable,
                            Array.Empty<MarketEvent>(), Array.Empty<HistoricalRecordingLoss>(), null, false));
                }
                var rows = _events.Where(e => e.Source == query.Source && e.Kind == query.Kind
                    && e.Ticker == query.Ticker && (!query.Expiration.HasValue || e.Expiration == query.Expiration)
                    && (!query.ConId.HasValue || e.Contract?.ConId == query.ConId)
                    && (!query.TradeScope.HasValue || e.TradeScope == query.TradeScope)
                    && e.ReceivedUtc >= query.FromUtc && e.ReceivedUtc < query.ToUtc)
                    .OrderBy(e => e.ReceivedUtc).ThenBy(e => e.RecordingRunId).ThenBy(e => e.ReceiveSequence).ToArray();
                var page = rows.Skip(offset).Take(query.Limit).ToArray();
                var next = offset + page.Length < rows.Length ? $"{Writes}:{offset + page.Length}" : null;
                return ValueTask.FromResult(new HistoricalReadResult(HistoricalReadStatus.Available,
                    Array.AsReadOnly(page), Array.AsReadOnly(_losses.Where(p => p.Value.Revision != 0)
                        .Select(p => new HistoricalRecordingLoss(p.Key, p.Value)).ToArray()), next, false));
            }
        }
    }
    private sealed class SlowStore : IMarketEventBatchWriter
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly MemoryStore Memory = new();
        public bool Fail;
        public async ValueTask WriteAsync(MarketRecordingBatch batch, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("Synthetic writer failure, never copied to status");
            await Memory.WriteAsync(batch, token);
        }
    }

    public static void DisabledAndContracts()
    {
        Check(MarketEventHub.Current == null, "Production recorder is disabled by default");
        var value = Event(Guid.NewGuid(), 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
        {
            _ = MarketEventHub.Current;
            _ = DisabledMarketEventSink.Instance.TryPublish(value);
        }
        // Singleton initialization occurs before the measured verification loop.
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) _ = DisabledMarketEventSink.Instance.TryPublish(value);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Disabled recording allocates no event/worker/queue");
        var cells = new[] { new MarketStrikeValue(700, 4) };
        var frame = new MarketProviderValues(700, cells, T);
        cells[0] = new(701, 8);
        Check(frame.Strikes[0].Strike == 700 && ((IList<MarketStrikeValue>)frame.Strikes).IsReadOnly, "Provider payload owns immutable copy");
        var query = new HistoricalQuery(MarketEventSource.Ib, MarketEventKind.OptionOpenInterest, "QQQ", null, 1, T, T.AddMinutes(1));
        Check(DisabledHistoricalDataReader.Instance.ReadAsync(query, default).Result.Status == HistoricalReadStatus.Disabled,
            "No database/readback is enabled implicitly");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = false;
        try { _ = DisabledHistoricalDataReader.Instance.ReadAsync(query, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Reader honors cancellation before work");
        var invalid = false;
        try { (query with { ToUtc = T }).Validate(); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "Empty/reversed range rejected");
        _ = PerformanceDiagnosticTests.LoadPro(); // Offline resolver for inherited ATAS types.
        var standard = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
            "src/FuturesReferencePriceAxis/bin/Release/FuturesReferencePriceAxis.dll"));
        Check(!standard.GetTypes().Any(t => t.Namespace?.StartsWith("WolfMoss.MarketData", StringComparison.Ordinal) == true), "Standard edition contains no history types");
    }

    public static void CongestionAndFlush() => CongestionAndFlushAsync().GetAwaiter().GetResult();
    private static async Task CongestionAndFlushAsync()
    {
        var store = new SlowStore();
        await using var sink = new BufferedMarketEventSink(store, 2, 1, TimeSpan.FromMilliseconds(5));
        Check(sink.TryPublish(Event(sink.RecordingRunId, 1)) == MarketEventAcceptance.Accepted, "Admission");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(sink.TryPublish(Event(sink.RecordingRunId, 2)) == MarketEventAcceptance.Accepted
            && sink.TryPublish(Event(sink.RecordingRunId, 3)) == MarketEventAcceptance.Accepted, "Bounded pending capacity");
        var rejected = 0;
        Parallel.For(4, 10004, i => { if (sink.TryPublish(Event(sink.RecordingRunId, i)) == MarketEventAcceptance.Congested) Interlocked.Increment(ref rejected); });
        Check(rejected == 10000 && sink.Status.Queued == 2 && !store.Release.Task.IsCompleted,
            "Slow storage never blocks admission or grows pending queue");
        store.Release.SetResult();
        await sink.DisposeAsync();
        var status = sink.Status;
        Check(status.Written == 3 && status.Queued == 0 && status.Loss.Congested == 10000, "Accepted records drain, rejected records counted");
        var result = await store.Memory.ReadAsync(new(MarketEventSource.Ib, MarketEventKind.OptionOpenInterest,
            "QQQ", null, 1, T, T.AddMinutes(1)), default);
        Check(result.RecordingLosses.Single().Loss.Congested == 10000 && !result.SourceContinuityKnown,
            "Gap ledger persists even without a later market event");
        Check(sink.TryPublish(Event(sink.RecordingRunId, 4)) == MarketEventAcceptance.Disabled, "Closed sink refuses new input");
        var finalStore = new SlowStore();
        await using var finalSink = new BufferedMarketEventSink(finalStore, 2, 1, TimeSpan.FromMilliseconds(5));
        finalSink.TryPublish(Event(finalSink.RecordingRunId, 1));
        await finalStore.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(finalSink.TryPublish(Event(Guid.NewGuid(), 2)) == MarketEventAcceptance.Faulted, "Wrong recording identity rejected");
        var stopping = finalSink.DisposeAsync().AsTask();
        finalStore.Release.SetResult(); await stopping;
        var finalResult = await finalStore.Memory.ReadAsync(new(MarketEventSource.Ib, MarketEventKind.OptionOpenInterest,
            "QQQ", null, 1, T, T.AddMinutes(1)), default);
        Check(finalResult.RecordingLosses.Single().Loss.CaptureFailed == 1, "Loss arriving during final in-flight write is flushed before normal exit");
    }

    public static void FailureAndShutdown() => FailureAndShutdownAsync().GetAwaiter().GetResult();
    private static async Task FailureAndShutdownAsync()
    {
        var store = new SlowStore { Fail = true };
        await using var sink = new BufferedMarketEventSink(store, 2, 1, TimeSpan.FromMilliseconds(5));
        sink.TryPublish(Event(sink.RecordingRunId, 1)); await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sink.TryPublish(Event(sink.RecordingRunId, 2)); store.Release.SetResult();
        await Until(() => sink.Status.IsStopped);
        Check(sink.Status.IsFaulted && sink.Status.Loss.WriteFailed == 1 && sink.Status.Loss.UndeliveredOnStop == 1,
            "Fault reports failed in-flight and undelivered queued events, without retry storm");
        Check(sink.TryPublish(Event(sink.RecordingRunId, 3)) == MarketEventAcceptance.Faulted, "Persistent fault result");
        var slow = new SlowStore();
        await using var closing = new BufferedMarketEventSink(slow, 2, 1, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(80));
        closing.TryPublish(Event(closing.RecordingRunId, 1)); await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var time = Stopwatch.StartNew();
        await Task.WhenAll(closing.DisposeAsync().AsTask(), closing.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
        Check(time.Elapsed < TimeSpan.FromSeconds(2) && closing.Status.Loss.UndeliveredOnStop == 1, "Cooperative slow writer cancels within bounded shutdown");
    }

    public static void IbSharedIntake() => IbSharedIntakeAsync().GetAwaiter().GetResult();
    private static async Task IbSharedIntakeAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        await using var cleanup = client;
        var memory = new MemoryStore();
        await using var sink = new BufferedMarketEventSink(memory, 64, 16, TimeSpan.FromMilliseconds(5));
        await using var attach = MarketEventHub.Attach(sink);
        var contract = new OptionContractDescriptor(1, "QQQ", new(2026, 9, 1), 700, OptionRight.Call,
            "QQQ", "SMART", 100, "20260901:0930-20260901:1600", "America/New_York");
        var calls = 0;
        using var a = await client.SubscribeAsync([contract], new(true, true, true), _ => calls++, 2, default);
        using var b = await client.SubscribeAsync([contract], new(true, true, true), _ => calls++, 2, default);
        var id = socket.Operations.Last(x => x.Op == "request").Id;
        void Callback(string name, params object[] values) => typeof(ReflectionIbOptionGatewayClient)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client, [values]);
        void Tick(int field, int seconds, long total) => Callback("OnTickString", id, field,
            $"2;1;{new DateTimeOffset(T.AddSeconds(seconds)).ToUnixTimeMilliseconds()};{total};2;false");
        Callback("OnTickSize", id, 27, 0m);
        Tick(77, 1, 100); Tick(48, 2, 500); Tick(77, 3, 101); Tick(77, 4, 2); Tick(77, 2, 1);
        Callback("OnTickString", id, 77, "2;1;invalid;200;2;false");
        Check(calls == 12, "Six valid source callbacks fan out to two charts; invalid source time is rejected");
        await attach.DisposeAsync();
        var events = memory.Events;
        Check(events.Length == 7 && events.Select(e => e.ReceiveSequence).Distinct().Count() == 7,
            "One historical event per shared reception plus an explicit malformed-input boundary");
        Check(events[0].OpenInterest == 0 && events[0].SourceUtc == null && events[0].Contract!.ConId == 1,
            "OI zero remains valid; receive time is not invented source time");
        Check(events[1].SourceUtc == T.AddSeconds(1) && events[1].Cumulative!.TotalVolume == 100
            && events[2].TradeScope == MarketTradeScope.AllTimeAndSales, "Exact source time, cumulative baseline and separate scopes");
        Check(events[4].Quality.HasFlag(MarketEventQuality.CounterReset)
            && events[5].Quality.HasFlag(MarketEventQuality.OutOfOrder), "Recording preserves reset and out-of-order quality without changing live values");
        Check(events[6].Boundary == MarketBoundaryReason.InvalidSourceSample && events[6].Cumulative == null,
            "Invalid source timestamp leaves a quality marker, not fabricated cumulative data");
        Check(events.All(e => e.ConnectionGeneration > 0 && e.ObservationGeneration == id
            && e.Trading!.ProviderTimeZone == "America/New_York"), "Connection/subscription identity and raw session provenance");
        Tick(77, 6, 3);
        Check(calls == 14 && memory.Events.Length == 7 && MarketEventHub.Current == null, "Detaching history leaves realtime flow alive");
    }

    public static void NightwatchDedupAndIsolation() => NightwatchDedupAndIsolationAsync().GetAwaiter().GetResult();
    private static async Task NightwatchDedupAndIsolationAsync()
    {
        var store = new MemoryStore();
        await using var sink = new BufferedMarketEventSink(store, 32, 8, TimeSpan.FromMilliseconds(5));
        await using var attach = MarketEventHub.Attach(sink);
        var coordinator = new ReferenceQuoteCoordinator<int, DealerHeatmapFrame>(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5));
        var frame = new DealerHeatmapFrame("QQQ", new(2026, 9, 1), T, 700, [new(700, 45)]);
        var downloads = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<DealerHeatmapFrame> Download(CancellationToken token)
        { Interlocked.Increment(ref downloads); await release.Task.WaitAsync(token); return NightwatchEventCapture.Heatmap(frame, T.AddMinutes(5)); }
        var tasks = Enumerable.Range(0, 8).Select(_ => coordinator.GetAsync(1, Download, default)).ToArray();
        release.SetResult(); await Task.WhenAll(tasks);
        await coordinator.GetAsync(1, Download, default);
        var gex = new DealerGexFrame("SPX", T, new(2026, 9, 1), "closed", 7700,
            [new(7700, -12, "King", 1, 1)], new(-12, 7700, 7695, 7705, 7685, null, 7700));
        NightwatchEventCapture.DealerGex(gex, T.AddMinutes(6));
        // Conversion failure is contained and counted, not allowed to clear a valid display frame.
        var oversized = frame with { Cells = Enumerable.Range(0, 2049).Select(i => new DealerHeatmapCell(i + 1, 0)).ToArray() };
        Check(ReferenceEquals(oversized, NightwatchEventCapture.Heatmap(oversized, T)), "Storage conversion cannot affect valid realtime object");
        await attach.DisposeAsync();
        var rows = store.Events;
        Check(downloads == 1 && rows.Length == 2, "Shared cache hits do not duplicate provider recording");
        Check(rows[0].SourceUtc == T.AddMinutes(4) && rows[0].Provider!.BucketStartUtc == T,
            "Sample time and provider bucket start are both retained");
        Check(rows[1].Expiration == null && rows[1].Trading!.SessionDate == new DateOnly(2026, 9, 1)
            && rows[1].Provider!.Summary!.GammaFlip == 7695, "GEX session date is not misrepresented as expiration");
        Check(sink.Status.Loss.CaptureFailed == 1, "Failed conversion exposes recording loss");
        var pro = PerformanceDiagnosticTests.LoadPro();
        var hub = pro.GetType("WolfMoss.MarketData.MarketEventHub", true)!;
        Check(hub.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null) == null,
            "Test module recording cannot enable Pro or another plugin's recorder");
    }

    public static void SourceBoundariesAndFailure() => SourceBoundariesAndFailureAsync().GetAwaiter().GetResult();
    private static async Task SourceBoundariesAndFailureAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        await using var cleanup = client;
        var memory = new MemoryStore();
        await using var sink = new BufferedMarketEventSink(memory, 32, 8, TimeSpan.FromMilliseconds(5));
        await using var attached = MarketEventHub.Attach(sink);
        var c = new OptionContractDescriptor(1, "QQQ", new(2026, 9, 1), 700, OptionRight.Call, "QQQ", "SMART", 100, "");
        var calls = 0;
        using var lease = await client.SubscribeAsync([c], new(true, false, false), _ => calls++, 2, default);
        var tick = typeof(ReflectionIbOptionGatewayClient).GetMethod("OnTickSize", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldId = socket.Operations.Last(x => x.Op == "request").Id;
        tick.Invoke(client, [new object[] { oldId, 27, 2m }]);
        await lease.UpdateAsync([c], new(true, true, false), 2, default);
        var newId = socket.Operations.Last(x => x.Op == "request").Id;
        tick.Invoke(client, [new object[] { oldId, 27, 10m }]); // retired request ignored
        tick.Invoke(client, [new object[] { newId, 27, 3m }]);
        lease.Dispose(); await attached.DisposeAsync();
        var rows = memory.Events;
        Check(rows.Count(e => e.Kind == MarketEventKind.OptionOpenInterest) == 2 && calls == 2, "Retired callback cannot populate current generation");
        Check(rows.Any(e => e.Boundary == MarketBoundaryReason.SubscriptionReplaced && e.ObservationGeneration == oldId)
            && rows.Any(e => e.Boundary == MarketBoundaryReason.SubscriptionEnded && e.ObservationGeneration == newId), "Explicit source observation boundaries");
        Check(rows.Last(e => e.Kind == MarketEventKind.OptionOpenInterest).Quality.HasFlag(MarketEventQuality.ObservationStart), "Replacement begins new observation");
        var broken = new SlowStore { Fail = true }; broken.Release.SetResult();
        await using var failedSink = new BufferedMarketEventSink(broken, 8, 4, TimeSpan.FromMilliseconds(5));
        await using var failedAttach = MarketEventHub.Attach(failedSink);
        using var fresh = await client.SubscribeAsync([c], new(true, false, false), _ => calls++, 2, default);
        var freshId = socket.Operations.Last(x => x.Op == "request").Id;
        tick.Invoke(client, [new object[] { freshId, 27, 4m }]);
        await Until(() => failedSink.Status.IsFaulted);
        tick.Invoke(client, [new object[] { freshId, 27, 5m }]);
        Check(calls == 4 && client.IsConnected && client.ActiveMarketDataLines == 1, "Failed writer cannot disconnect or suppress realtime callbacks");
        Check(MarketEventHub.Current == null, "Writer fault also disables further input materialization");
    }

    public static void QueryAndRecorderOwnership() => QueryAndRecorderOwnershipAsync().GetAwaiter().GetResult();
    private static async Task QueryAndRecorderOwnershipAsync()
    {
        var memory = new MemoryStore();
        await using var sink = new BufferedMarketEventSink(memory, 8, 4, TimeSpan.FromMilliseconds(5));
        await using var attach = MarketEventHub.Attach(sink);
        await using var other = new BufferedMarketEventSink(new MemoryStore(), 8, 4);
        var collision = false;
        try { _ = MarketEventHub.Attach(other); } catch (InvalidOperationException) { collision = true; }
        Check(collision, "Only one recording owner per source module");
        for (var i = 1; i <= 4; i++) sink.TryPublish(Event(sink.RecordingRunId, i));
        await attach.DisposeAsync();
        var query = new HistoricalQuery(MarketEventSource.Ib, MarketEventKind.OptionOpenInterest, "QQQ",
            new(2026, 9, 1), 1, T.AddSeconds(1), T.AddSeconds(4), Limit: 2);
        var page = await memory.ReadAsync(query, default);
        Check(page.Events.Count == 2 && page.ContinuationToken != null && page.Events[0].ReceiveSequence == 1, "Contract/range filtering and bounded page");
        var next = await memory.ReadAsync(query with { ContinuationToken = page.ContinuationToken }, default);
        Check(next.Events.Count == 1 && next.Events[0].ReceiveSequence == 3 && next.ContinuationToken == null, "Exclusive end, deterministic continuation");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var didCancel = false;
        try { await memory.ReadAsync(query, canceled.Token); } catch (OperationCanceledException) { didCancel = true; }
        Check(didCancel, "Active reader cancellation");
        await using var replacement = MarketEventHub.Attach(other);
        Check(MarketEventHub.Current!.RunId != sink.RecordingRunId, "New recorder has separate run/sequence identity");
        await attach.DisposeAsync();
        Check(MarketEventHub.Current!.RunId == other.RecordingRunId, "Old attachment cannot detach new recorder");
    }
}
