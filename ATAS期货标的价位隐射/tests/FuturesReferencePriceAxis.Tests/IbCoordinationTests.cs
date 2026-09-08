using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class IbCoordinationTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Set(object owner, string name, object value)
        => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, value);
    private static object Get(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static Task Drain(ReflectionIbOptionGatewayClient client)
        => ((IbOutboundDispatcher)Get(client, "_outbound")).Enqueue(() => { });
    private static void LoadApi()
    {
        var pro = PerformanceDiagnosticTests.LoadPro();
        pro.GetType("WolfMoss.ATAS.PriceMapping.EmbeddedDependencyResolver")!
            .GetMethod("TryLoadIbApiAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    }

    // No socket/network: the exact production gateway source talks to this recorder.
    public sealed class FakeSocket
    {
        public bool IsConnected { get; private set; } = true;
        public ConcurrentQueue<(string Op, int Id, string Ticks)> Operations { get; } = new();
        public ConcurrentDictionary<int, byte> Lines { get; } = new();
        public int MaximumLines;
        public bool FailCancel;
        public void reqMktData(int id, object contract, string ticks, bool snapshot, bool regulatory, object options)
        {
            Lines[id] = 0; MaximumLines = Math.Max(MaximumLines, Lines.Count);
            Operations.Enqueue(("request", id, ticks));
        }
        public void cancelMktData(int id)
        {
            if (FailCancel) throw new IOException("Simulated socket send failure");
            Lines.TryRemove(id, out _); Operations.Enqueue(("cancel", id, ""));
        }
        public void eDisconnect() { IsConnected = false; Lines.Clear(); }
    }
    internal static (ReflectionIbOptionGatewayClient Client, FakeSocket Socket) Connected()
    {
        LoadApi();
        var client = new ReflectionIbOptionGatewayClient(new("test.invalid", 1, 9910));
        var socket = new FakeSocket();
        Set(client, "_client", socket);
        Set(client, "_initialization", Task.CompletedTask);
        ((TaskCompletionSource)Get(client, "_connected")).TrySetResult();
        return (client, socket);
    }
    private static OptionContractDescriptor Contract(int id) => new(id, "QQQ",
        new DateOnly(2026, 9, 1), 700 + id / 2m, id % 2 == 0 ? OptionRight.Call : OptionRight.Put,
        "QQQ", "SMART", 100, "", "America/New_York");

    public static void Dispatcher() => DispatcherAsync().GetAwaiter().GetResult();
    private static async Task DispatcherAsync()
    {
        var times = new ConcurrentQueue<long>();
        await using var sender = new IbOutboundDispatcher(() => times.Enqueue(Stopwatch.GetTimestamp()));
        var sent = 0;
        var tasks = Enumerable.Range(0, 91).Select(_ => sender.Enqueue(() => sent++)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(6));
        Check(sent == 91, "All paced messages sent");
        var stamps = times.ToArray();
        for (var i = 45; i < stamps.Length; i++)
            Check(Stopwatch.GetElapsedTime(stamps[i - 45], stamps[i]).TotalMilliseconds >= 995,
                "No more than 45 requests per sliding second");
        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await sender.Enqueue(() => sent++, cancellation.Token); } catch (OperationCanceledException) { }
        await sender.Enqueue(() => sent++, isCurrent: () => false);
        Check(sent == 91, "Canceled and stale commands never send");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = new ManualResetEventSlim();
        var bounded = new IbOutboundDispatcher(() => { }, capacity: 1);
        var first = bounded.Enqueue(() => { entered.SetResult(); gate.Wait(); });
        await entered.Task;
        var second = bounded.Enqueue(() => { });
        var overflow = bounded.Enqueue(() => throw new Exception());
        Check(overflow.IsFaulted, "Bounded admission rejects overflow without blocking");
        try { await overflow; } catch (InvalidOperationException) { }
        var disposal = bounded.DisposeAsync().AsTask();
        gate.Set();
        await first;
        try { await second; } catch (OperationCanceledException) { }
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        await bounded.DisposeAsync();
    }

    public static void SingleFlight() => SingleFlightAsync().GetAwaiter().GetResult();
    private static async Task SingleFlightAsync()
    {
        var requests = new SharedAsyncRequests<int, int>();
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task<int> Factory() { Interlocked.Increment(ref count); return result.Task; }
        using var cancel = new CancellationTokenSource();
        var one = requests.GetAsync(1, Factory, cancel.Token);
        var others = Enumerable.Range(0, 40).Select(_ => requests.GetAsync(1, Factory, default)).ToArray();
        cancel.Cancel();
        try { await one; } catch (OperationCanceledException) { }
        result.SetResult(42);
        Check((await Task.WhenAll(others)).All(value => value == 42) && count == 1, "One discovery, isolated waiter cancellation");
        await requests.GetAsync(1, Factory, default);
        Check(count == 1, "Successful result memoized");
        try { await requests.GetAsync(2, () => Task.FromException<int>(new IOException()), default); } catch (IOException) { }
        Check(await requests.GetAsync(2, () => Task.FromResult(9), default) == 9, "Failed discovery can retry");
    }

    public static void Subscriptions() => SubscriptionsAsync().GetAwaiter().GetResult();
    private static async Task SubscriptionsAsync()
    {
        var (client, socket) = Connected();
        await using var cleanup = client;
        var contracts = Enumerable.Range(1, 42).Select(Contract).ToArray();
        var demand = new IbOptionSubscriptionRequirements(true, true, false);
        using var first = await client.SubscribeAsync(contracts, demand, _ => { }, 42, default);
        using var second = await client.SubscribeAsync(contracts, demand, _ => { }, 42, default);
        Check(socket.Operations.Count == 42 && client.ActiveMarketDataLines == 42, "Two charts share 42 requests");
        first.Dispose();
        await Drain(client);
        Check(socket.Operations.Count == 42, "Releasing one chart leaves the other intact");
        var moved = Enumerable.Range(3, 42).Select(Contract).ToArray();
        await second.UpdateAsync(moved, demand, 42, default);
        await Drain(client);
        Check(socket.Operations.Count == 46, "ATM move sends only 2 edge cancels and 2 additions");
        Check(socket.MaximumLines == 42 && client.ActiveMarketDataLines == 42, "No transient budget overshoot");
        var subscriptionLock = Get(client, "_sync");
        Task<int> reader;
        lock (subscriptionLock)
        {
            reader = Task.Run(() => client.ActiveMarketDataLines);
            Check(reader.Wait(TimeSpan.FromSeconds(1)), "Status reads do not wait for the subscription lock");
        }
        Check(await reader == 42, "Atomic line snapshot");
        second.Dispose();
        await Drain(client);
        Check(client.ActiveMarketDataLines == 0 && socket.Lines.IsEmpty, "Last consumer cancels all retained lines");
        Check(client.PerformanceSnapshot.Consumers == 0, "No consumer leak");
        using var qqq = await client.SubscribeAsync(contracts, demand, _ => { }, 84, default);
        var spxContracts = Enumerable.Range(101, 42).Select(id => Contract(id) with
            { Ticker = "SPX", TradingClass = "SPXW" }).ToArray();
        using var spx = await client.SubscribeAsync(spxContracts, demand, _ => { }, 84, default);
        Check(client.ActiveMarketDataLines == 84 && socket.MaximumLines == 84, "QQQ and SPX fit 84 lines together");
        spx.Dispose();
        await Drain(client);
        Check(client.ActiveMarketDataLines == 42, "Releasing SPX preserves QQQ subscriptions");
    }

    public static void CancellationAndDemands() => CancellationAndDemandsAsync().GetAwaiter().GetResult();
    private static async Task CancellationAndDemandsAsync()
    {
        var (client, socket) = Connected();
        await using var cleanup = client;
        var contracts = new[] { Contract(1), Contract(2) };
        using var oi = await client.SubscribeAsync(contracts, new(true, false, false), _ => { }, 2, default);
        using var flow = await client.SubscribeAsync(contracts, new(false, true, false), _ => { }, 2, default);
        Check(socket.Operations.Where(op => op.Op == "request").TakeLast(2).All(op => op.Ticks == "101,375"), "OI/Flow ticks union on same lines");
        flow.Dispose();
        await Drain(client);
        Check(socket.Operations.Where(op => op.Op == "request").TakeLast(2).All(op => op.Ticks == "101"), "Released demand removed");
        Check(client.ActiveMarketDataLines == 2 && socket.MaximumLines == 2, "Union does not add lines");
        var before = socket.Operations.Count;
        try { await oi.UpdateAsync(Enumerable.Range(1, 3).Select(Contract).ToArray(), new(true, false, false), 2, default); }
        catch (IbOptionGatewayException ex) when (ex.Code == "LINE_LIMIT") { }
        Check(socket.Operations.Count == before && oi.ContractCount == 2, "Budget rejection is atomic");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try { await client.SubscribeAsync(contracts, new(true, false, false), _ => { }, 2, cancel.Token); } catch (OperationCanceledException) { }
        Check(client.PerformanceSnapshot.Consumers == 1, "Canceled consumer not attached");
    }

    public static void Pool() => PoolAsync().GetAwaiter().GetResult();
    public static void PendingCancellationAndFailure() => PendingCancellationAndFailureAsync().GetAwaiter().GetResult();
    private static async Task PendingCancellationAndFailureAsync()
    {
        var (client, socket) = Connected();
        await using var cleanup = client;
        var sender = (IbOutboundDispatcher)Get(client, "_outbound");
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = sender.Enqueue(() => { entered.SetResult(); gate.Wait(); });
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var pending = client.SubscribeAsync(Enumerable.Range(1, 42).Select(Contract).ToArray(),
            new(true, false, false), _ => { }, 42, cancel.Token);
        Check(client.ActiveMarketDataLines == 42, "Pending requests reserve budget");
        cancel.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); } catch (OperationCanceledException) { }
        Check(client.ActiveMarketDataLines == 0, "Cancellation frees reservations without waiting on sender");
        gate.Set();
        await blocker;
        await Drain(client);
        Check(!socket.Operations.Any(op => op.Op == "request"), "Stale queued subscriptions never sent");
        var lease = await client.SubscribeAsync(new[] { Contract(1) }, new(true, false, false), _ => { }, 42, default);
        socket.FailCancel = true;
        lease.Dispose();
        var deadline = Stopwatch.StartNew();
        while (socket.IsConnected && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        Check(client.ConnectionFaulted && !socket.IsConnected && socket.Lines.IsEmpty,
            "Failed cancellation retires connection rather than leaking a server subscription");
    }
    public static void FairBudget()
    {
        var budget = new OptionFlowLineBudget();
        Check(budget.Update(1, "QQQ", 21, 84) == 21, "Single target gets 21");
        Check(budget.Update(2, "SPX", 21, 84) == 21, "84 lines fund both targets");
        Check(budget.Update(3, "QQQ", 21, 84) == 21, "Duplicate ticker does not consume another slice");
        budget.Update(2, "SPX", 21, 42);
        Check(budget.Update(1, "QQQ", 21, 84) == 9
            && budget.Update(2, "SPX", 21, 42) == 9, "42 lines split fairly into symmetric 9-strike ladders");
        Check(budget.EffectiveBudget(500) == 42, "Highest-budget instance cannot override the shared lower limit");
        budget.Update(2, "SPX", 0, 42);
        Check(budget.Update(1, "QQQ", 21, 84) == 21, "A closed target takes no Flow slice");
        budget.Remove(2);
        Check(budget.Update(1, "QQQ", 21, 84) == 21, "Released ticker returns capacity");
        budget.Update(2, "SPX", 21, 10);
        Check(budget.Update(1, "QQQ", 21, 84) == 1, "Tiny budget retains ATM symmetrically");
    }

    public static void CallbackAllocation() => CallbackAllocationAsync().GetAwaiter().GetResult();
    private static async Task CallbackAllocationAsync()
    {
        var (client, _) = Connected();
        await using var cleanup = client;
        var calls = 0;
        using var lease = await client.SubscribeAsync(new[] { Contract(1) }, new(true, false, false),
            _ => calls++, 84, default);
        var dictionary = (System.Collections.IDictionary)Get(client, "_subscriptionsByConId");
        var shared = dictionary[1L]!;
        var method = typeof(ReflectionIbOptionGatewayClient).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Static)!;
        var arg = Expression.Parameter(typeof(IbOptionMarketDataUpdate));
        var invoke = Expression.Lambda<Action<IbOptionMarketDataUpdate>>(
            Expression.Call(method, Expression.Constant(shared, shared.GetType()), arg), arg).Compile();
        for (var i = 0; i < 100; i++) invoke(default);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) invoke(default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(allocated == 0 && calls == 10100, "Steady fanout allocates no per-event consumer array");
        lease.Dispose();
        invoke(default);
        Check(calls == 10100, "Released subscription publishes to no consumers");
        Console.WriteLine($"ib_callback_fanout: events=10000, allocated_bytes={allocated}");
    }

    public static void IndicatorRapidSwitch()
    {
        var pro = PerformanceDiagnosticTests.LoadPro();
        var type = pro.GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator")!;
        var indicator = RuntimeHelpers.GetUninitializedObject(type);
        Set(indicator, "_optionScheduleSync", new object());
        using var lifetime = new CancellationTokenSource();
        Set(indicator, "_optionLifetimeCancellation", lifetime);
        var previous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(indicator, "_optionLoopTask", previous.Task);
        type.BaseType!.GetField("_initialized", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(indicator, true);
        var restart = type.GetMethod("RestartOptionDataSchedule", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Prevent any generation from reaching data/network initialization. All 30
        // starts are canceled before the predecessor is allowed to exit.
        for (var i = 0; i < 30; i++)
        {
            Set(indicator, "_showOptionOpenInterest", true);
            Set(indicator, "_showOptionPremiumFlow", true);
            restart.Invoke(indicator, new object[] { false, false });
            Set(indicator, "_showOptionOpenInterest", false);
            Set(indicator, "_showOptionPremiumFlow", false);
            restart.Invoke(indicator, new object[] { false, false });
        }
        var tail = (Task)Get(indicator, "_optionLoopTask");
        Check(!tail.IsCompleted, "Successors wait for predecessor retirement");
        previous.SetResult();
        tail.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Check(Get(indicator, "_optionScheduleCancellation") == null
            && Get(indicator, "_optionGatewayLease") == null, "All canceled generations leave no gateway or schedule");
    }
    private static async Task PoolAsync()
    {
        LoadApi();
        var options = new IbGatewayConnectionOptions("offline.test", 1, 9909);
        var leases = Enumerable.Range(0, 50).AsParallel().Select(_ => IbOptionGatewayPool.Acquire(options)).ToArray();
        var client = leases[0].Client;
        Check(leases.All(lease => ReferenceEquals(lease.Client, client)), "Concurrent acquire creates only one gateway");
        await Task.WhenAll(leases.Take(49).Select(lease => lease.DisposeAsync().AsTask()));
        Check(!((ReflectionIbOptionGatewayClient)client).ConnectionFaulted, "Remaining owner stays valid");
        await leases[49].DisposeAsync();
        await leases[49].DisposeAsync();
        var fresh = IbOptionGatewayPool.Acquire(options);
        Check(!ReferenceEquals(fresh.Client, client), "Last release removes pool entry");
        // A cold client never calls Connect: these tests cannot open a socket.
        var other = IbOptionGatewayPool.Acquire(options with { ClientId = 9911 });
        Check(!ReferenceEquals(other.Client, fresh.Client), "Distinct client IDs remain independent");
        Set(fresh.Client, "_connectionFaulted", 1);
        var replacement = IbOptionGatewayPool.Acquire(options);
        Check(!ReferenceEquals(fresh.Client, replacement.Client), "Faulted gateway replaced");
        var barrier = (Task)Get(replacement.Client, "_previousDisposal");
        await barrier.WaitAsync(TimeSpan.FromSeconds(3));
        await fresh.DisposeAsync();
        await replacement.DisposeAsync();
        await other.DisposeAsync();
    }
}
