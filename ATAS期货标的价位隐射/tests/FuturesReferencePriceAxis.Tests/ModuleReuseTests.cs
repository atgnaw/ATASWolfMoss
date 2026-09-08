using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class ModuleReuseTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static Assembly Consumer(string name)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "tests", "IbModule.Consumer" + name,
            "bin", configuration, "net10.0", "WolfMoss.IB.ReuseProbe" + name + ".dll"));
    }
    private static object Call(Assembly owner, string name, params object[] args)
        => owner.GetType("ModuleProbe", true)!.GetMethod(name)!.Invoke(null, args)!;
    private static void Conflict(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex.GetBaseException().Message.Contains("CLIENT_ID_IN_USE", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Duplicate Client ID must be refused");
    }

    public static void PrivateDependencies()
    {
        // A foreign same-name assembly must never be mistaken for our embedded API.
        var foreign = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CSharpAPI") { Version = new(99, 0) }, AssemblyBuilderAccess.Run);
        var a = Consumer("A"); var b = Consumer("B");
        var apiA = (Assembly)Call(a, "Api"); var apiB = (Assembly)Call(b, "Api");
        var pro = PerformanceDiagnosticTests.LoadPro();
        var apiPro = (Assembly)pro.GetType("WolfMoss.ATAS.PriceMapping.EmbeddedDependencyResolver", true)!
            .GetMethod("TryLoadIbApiAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var apis = new[] { apiA, apiB, apiPro };
        Check(apis.Distinct().Count() == 3 && apis.All(x => !ReferenceEquals(x, foreign)), "Each consumer owns its API, not the foreign namesake");
        var contexts = apis.Select(AssemblyLoadContext.GetLoadContext).ToArray();
        Check(contexts.Distinct().Count() == 3 && contexts.All(x => x != AssemblyLoadContext.Default), "Private contexts are independent");
        foreach (var api in apis)
        {
            Check(api.Location.Length == 0 && api.GetName().Version!.Major == 10, "Embedded official API");
            // Force generated message code to resolve Google.Protobuf inside its own context.
            var message = Activator.CreateInstance(api.GetType("IBApi.protobuf.MarketDataRequest", true)!);
            _ = message!.ToString();
        }
        var protobufs = contexts.Select(x => x!.Assemblies.Single(y => y.GetName().Name == "Google.Protobuf")).ToArray();
        Check(protobufs.Distinct().Count() == 3 && protobufs.All(x => x.Location.Length == 0), "Protobuf is private too");
        foreach (var consumer in new[] { a, b })
        {
            Check(!consumer.GetReferencedAssemblies().Any(x => x.Name!.StartsWith("ATAS", StringComparison.Ordinal)
                || x.Name.StartsWith("OFT", StringComparison.Ordinal) || x.Name.StartsWith("FuturesReference", StringComparison.Ordinal)),
                "Module compiles without ATAS/Pro");
            var socket = Call(consumer, "CreateUnconnectedSocket");
            Check(ReferenceEquals(socket.GetType().Assembly, Call(consumer, "Api")), "Socket uses owning API");
        }
        Check(a.GetType("WolfMoss.ATAS.PriceMapping.IIbOptionGatewayClient") != null
            && b.GetType("WolfMoss.ATAS.PriceMapping.IIbOptionGatewayClient") == null, "Options can be excluded for other adapters");
        var countA = 0; var countB = 0;
        var callbackA = Call(a, "CreateCallbackProxy", (Action)(() => countA++));
        var callbackB = Call(b, "CreateCallbackProxy", (Action)(() => countB++));
        Check(AssemblyLoadContext.GetLoadContext(callbackA.GetType().Assembly) == contexts[0]
            && AssemblyLoadContext.GetLoadContext(callbackB.GetType().Assembly) == contexts[1], "Generated proxies belong to their SDK context");
        Check(Call(a, "CreateCallbackProxy", (Action)(() => { })).GetType() == callbackA.GetType(), "Repeated connection reuses generated proxy type");
        Check(apiA.GetType("IBApi.EWrapper")!.IsInstanceOfType(callbackA)
            && apiB.GetType("IBApi.EWrapper")!.IsInstanceOfType(callbackB)
            && !apiA.GetType("IBApi.EWrapper")!.IsInstanceOfType(callbackB), "Proxies bind only to their own SDK interface");
        apiA.GetType("IBApi.EWrapper")!.GetMethod("nextValidId")!.Invoke(callbackA, [7]);
        apiB.GetType("IBApi.EWrapper")!.GetMethod("nextValidId")!.Invoke(callbackB, [9]);
        Check(countA == 1 && countB == 1, "Callbacks do not cross consumer assemblies");
    }

    public static void SessionReservations()
    {
        var a = Consumer("A"); var b = Consumer("B");
        using var first = (IDisposable)Call(a, "Reserve", "localhost", 4001, 99201);
        Conflict(() => Call(b, "Reserve", "127.0.0.1", 4001, 99201));
        using var independent = (IDisposable)Call(b, "Reserve", "127.0.0.1", 4001, 99202);
        first.Dispose(); first.Dispose();
        Conflict(() => Call(a, "Reserve", "::1", 4001, 99202));
        using var replacement = (IDisposable)Call(b, "Reserve", "[::1]", 4001, 99201);
        var winners = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        try
        {
            Parallel.For(0, 32, i =>
            {
                try { winners.Add((IDisposable)Call(i % 2 == 0 ? a : b, "Reserve", "127.0.0.1", 4001, 99203)); }
                catch (Exception ex) when (ex.GetBaseException().Message.Contains("CLIENT_ID_IN_USE", StringComparison.Ordinal)) { }
            });
            Check(winners.Count == 1, "Cross-assembly concurrent reservation has exactly one owner");
        }
        finally { foreach (var owner in winners) owner.Dispose(); }
        using var reused = (IDisposable)Call(a, "Reserve", "localhost", 4001, 99203);
    }

    public static void RequestIdsAndConflictCallback()
    {
        var sequence = new IbRequestIdSequence();
        var ids = new int[2000];
        Parallel.For(0, ids.Length, i => ids[i] = sequence.Next());
        Check(ids.Distinct().Count() == ids.Length && ids.Min() == 10001 && ids.Max() == 12000, "Atomic connection-local IDs");
        Check((int)Call(Consumer("A"), "FirstRequestId") == 10001 && (int)Call(Consumer("B"), "FirstRequestId") == 10001,
            "Different connections may safely use identical request IDs");
        var exhausted = new IbRequestIdSequence(int.MaxValue - 1);
        Check(exhausted.Next() == int.MaxValue, "Last valid ID");
        var threw = false;
        try { exhausted.Next(); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Request ID must not wrap into invalid values");
        var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 99210));
        try
        {
            typeof(ReflectionIbOptionGatewayClient).GetMethod("OnError", Private)!.Invoke(client, [new object[] { -1, 0L, 326, "in use", "" }]);
            var completion = typeof(ReflectionIbOptionGatewayClient).GetField("_connected", Private)!.GetValue(client)!;
            var task = (Task)completion.GetType().GetProperty("Task")!.GetValue(completion)!;
            Check(client.ConnectionFaulted && task.IsFaulted
                && task.Exception!.GetBaseException() is IbOptionGatewayException { Code: "CLIENT_ID_IN_USE" },
                "IB 326 fails connection promptly without taking over or incrementing Client ID");
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static void IndependentPools()
    {
        var a = Consumer("A");
        var first = Call(a, "AcquireOptions", "localhost", 4001, 99220);
        var second = Call(a, "AcquireOptions", "127.0.0.1", 4001, 99220);
        var own = IbOptionGatewayPool.Acquire(new("127.0.0.1", 4001, 99220));
        var peer = IbOptionGatewayPool.Acquire(new("localhost", 4001, 99220));
        object Client(object lease) => lease.GetType().GetProperty("Client")!.GetValue(lease)!;
        try
        {
            Check(ReferenceEquals(Client(first), Client(second)) && ReferenceEquals(own.Client, peer.Client), "Within-plugin pool shares aliases");
            Check(!ReferenceEquals(Client(first), own.Client), "Across plugins no socket/client pool is shared");
            ((IAsyncDisposable)first).DisposeAsync().AsTask().GetAwaiter().GetResult();
            ((IAsyncDisposable)second).DisposeAsync().AsTask().GetAwaiter().GetResult();
            Check(ReferenceEquals(own.Client, peer.Client) && own.Client.IsAvailable, "Other plugin release leaves own pool intact");
        }
        finally
        {
            ((IAsyncDisposable)first).DisposeAsync().AsTask().GetAwaiter().GetResult();
            ((IAsyncDisposable)second).DisposeAsync().AsTask().GetAwaiter().GetResult();
            own.DisposeAsync().AsTask().GetAwaiter().GetResult(); peer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static void ExtensibleColumns()
    {
        var definitions = DealerColumnCatalog.Default.Columns.Append(new((DealerColumnKind)99, (DealerColumnVisibility)16)).ToArray();
        var catalog = new DealerColumnCatalog(definitions);
        definitions[0] = definitions[4]; // catalog must own its copy
        for (var mask = 0; mask < 32; mask++)
        {
            var visibility = (DealerColumnVisibility)mask;
            var count = System.Numerics.BitOperations.PopCount((uint)mask);
            Check(catalog.Count(visibility) == count, "All visibility combinations including a fifth data column");
            var width = DealerColumnPlanner.CalculateColumnWidth(72, 420, visibility, catalog);
            var plan = DealerColumnPlanner.Create(width, width, visibility, catalog);
            Check(plan.ReservedWidth == count * width && (count + 1) * width <= 420, "Same width and reserved area");
            var index = 0;
            foreach (var definition in catalog.Columns)
            {
                var enabled = (visibility & definition.Visibility) != 0;
                Check(plan.TryGetLeft(definition.Kind, out var left) == enabled, "Disabled columns occupy no space");
                if (enabled) Check(left == width + index++ * width, "Registration order determines position");
            }
        }
        var rejected = false;
        try { _ = new DealerColumnCatalog([catalog.Columns[0], catalog.Columns[0]]); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Duplicate metadata rejected");
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var type = assembly.GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!;
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        var registrations = ((IEnumerable)type.GetField("RegisteredColumns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Cast<object>().ToArray();
        var lifecycles = (Array)type.GetField("RegisteredLifecycles", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(registrations.Length == 4 && lifecycles.Length == 2, "Four columns, two source lifecycles");
        var fields = new[] { "_showDealerHeatmap", "_showDealerGex", "_showOptionOpenInterest", "_showOptionPremiumFlow" };
        for (var mask = 0; mask < 16; mask++)
        {
            for (var i = 0; i < fields.Length; i++) type.GetField(fields[i], Private)!.SetValue(instance, (mask & (1 << i)) != 0);
            var visible = type.GetProperty("VisibleDealerColumns", Private)!.GetValue(instance)!;
            Check(Convert.ToInt32(visible) == mask, "Actual Pro visibility comes from registrations");
        }
        for (var i = 0; i < registrations.Length; i++)
        {
            var item = registrations[i];
            Check(Convert.ToInt32(item.GetType().GetProperty("Kind")!.GetValue(item)) == i, "Production column ordering unchanged");
            foreach (var member in new[] { "Snapshot", "Draw", "Tooltip", "Status", "Enabled", "Lifecycle" })
                Check(item.GetType().GetProperty(member)!.GetValue(item) != null, "Complete column descriptor: " + member);
        }
        for (var i = 0; i < 10000; i++) { catalog.Count((DealerColumnVisibility)31); catalog.TryGetVisibleIndex((DealerColumnVisibility)31, (DealerColumnKind)99, out _); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { catalog.Count((DealerColumnVisibility)31); catalog.TryGetVisibleIndex((DealerColumnVisibility)31, (DealerColumnKind)99, out _); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Catalog hot lookup allocates zero bytes");
    }

    public static void ExtensibleStatusCache()
    {
        var assembly = PerformanceDiagnosticTests.LoadPro();
        var type = assembly.GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator", true)!;
        var instance = RuntimeHelpers.GetUninitializedObject(type);
        var write = type.GetMethod("CacheEditionStatusLines", Private)!;
        var read = type.GetMethod("TryGetCachedEditionStatusLines", Private)!;
        var visible = Enum.ToObject(write.GetParameters()[0].ParameterType, 31);
        var snapshots = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
        var perf = new[] { "PERF" };
        var original = write.Invoke(instance, [visible, snapshots, 900, 42, new List<string> { "Fifth column" }, perf]);
        var args = new object?[] { visible, snapshots, perf, 900, 42, null };
        Check((bool)read.Invoke(instance, args)! && ReferenceEquals(original, args[5]), "Fifth column supports cache hits");
        var fifth = snapshots[4]; snapshots[4] = new object();
        Check(!(bool)read.Invoke(instance, args)!, "Fifth snapshot replacement invalidates cache, reusable input buffer is not aliased");
        snapshots[4] = fifth;
        Check((bool)read.Invoke(instance, args)!, "Stable snapshots reuse formatted lines");
        args[1] = snapshots[..4];
        Check(!(bool)read.Invoke(instance, args)!, "Column count invalidates cache");
    }
}
