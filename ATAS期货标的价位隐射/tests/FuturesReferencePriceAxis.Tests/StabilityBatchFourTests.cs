using System.Collections;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class StabilityBatchFourTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Get(object o, string n) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, v);
    public static void OiToggleDoesNotRestartFlow()
    {
        var fixture = new PerformanceBaseline.Fixture(PerformanceDiagnosticTests.LoadPro(), OptionFlowBucketMode.Rolling, 1, "QQQ", true);
        var indicator = fixture.Indicator;
        indicator.GetType().BaseType!.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(indicator, true);
        using var source = new CancellationTokenSource();
        Set(indicator, "_optionScheduleCancellation", source);
        Set(indicator, "_optionDataGeneration", 42L);
        var samples = Get(indicator, "_rollingFlow");
        var frame = Get(indicator, "_optionFlowSnapshot");
        for (var i = 0; i < 20; i++)
            indicator.GetType().GetProperty("ShowOptionOpenInterest")!.SetValue(indicator, i % 2 == 0);
        Check(!source.IsCancellationRequested && (long)Get(indicator, "_optionDataGeneration") == 42,
            "OI visibility cannot cancel/restart Flow generation");
        Check(ReferenceEquals(samples, Get(indicator, "_rollingFlow")) && ReferenceEquals(frame, Get(indicator, "_optionFlowSnapshot")),
            "OI toggles preserve Flow buffers and published snapshot");
    }
    public static void OiZeroQuality()
    {
        var fixture = new PerformanceBaseline.Fixture(PerformanceDiagnosticTests.LoadPro(), OptionFlowBucketMode.PreviousCompletedFixed, 1, "SPX", true);
        var indicator = fixture.Indicator;
        Set(indicator, "_showOptionOpenInterest", true);
        var values = (Dictionary<long, long>)Get(indicator, "_optionOpenInterest");
        var receipts = (Dictionary<long, DateTime>)Get(indicator, "_optionOiReceivedByContract");
        var at = new DateTime(2026, 9, 1, 15, 0, 4, DateTimeKind.Utc);
        for (var i = 1L; i <= 42; i++) { values[i] = 0; receipts[i] = at; }
        Set(indicator, "_optionOpenInterestReceivedUtc", at);
        fixture.PublishAt(at);
        var frame = Get(indicator, "_optionOpenInterestSnapshot");
        Check(frame.GetType().GetProperty("Message")!.GetValue(frame)!.ToString()!.Contains("全部为 0"), "All-zero frame warns without inventing missing values");
        Check(((IEnumerable)frame.GetType().GetProperty("Rows")!.GetValue(frame)!).Cast<object>().All(row => (long?)row.GetType().GetProperty("CallOpenInterest")!.GetValue(row) == 0), "Real zero OI preserved");
        values[1] = 10; Set(indicator, "_oiRevision", (long)Get(indicator, "_oiRevision") + 1);
        fixture.PublishAt(at.AddSeconds(1)); frame = Get(indicator, "_optionOpenInterestSnapshot");
        Check(!frame.GetType().GetProperty("Message")!.GetValue(frame)!.ToString()!.Contains("全部为 0"), "Warning clears on non-zero update");
    }
    public static void ContractHealthIsolation()
    {
        var health = new OptionInputHealth();
        var at = DateTime.UtcNow;
        var empty = OptionFlowSnapshot.Waiting(at, "test", OptionFlowBucketMode.Rolling, OptionFlowTradeScope.RegularTrades, 1);
        health.Error(1, "NO_PERMISSION");
        var oi = OptionOpenInterestSnapshot.Waiting(at, "test");
        Check(health.Apply(oi).Status == OptionDataStatus.NoPermission, "Missing OI permission failure is not overwritten by publication");
        var daily = oi with { Status = OptionDataStatus.Daily };
        Check(ReferenceEquals(health.Apply(daily), daily), "Flow error cannot invalidate confirmed daily OI");
        Check(health.Apply(empty).Status == OptionDataStatus.NoPermission, "Error remains visible across periodic publication");
        var populated = empty with { Rows = new[] { new OptionStrikeRow(710, null, null, 100, null, 1, null) } };
        Check(health.Apply(populated).Status == OptionDataStatus.Partial && health.Apply(populated).Rows[0].CallPremium == 100,
            "One contract failure does not clear other data");
        health.Received(1, true, false); Check(health.IsDelayed, "Delayed observed");
        health.Received(1, false, true);
        Check(!health.IsDelayed && ReferenceEquals(health.Apply(populated), populated), "Real-time data clears delayed and matching failure");
    }
    public static void BoundedSubscriptionRecovery() => RecoveryAsync().GetAwaiter().GetResult();
    private static async Task RecoveryAsync()
    {
        var (client, socket) = IbCoordinationTests.Connected();
        await using var cleanup = client;
        var contract = new OptionContractDescriptor(999, "QQQ", new(2026, 9, 1), 710, OptionRight.Call, "QQQ", "SMART", 100, "");
        using var lease = await client.SubscribeAsync([contract], new(true, true, false), _ => { }, 84, default);
        void Error(int code)
        {
            var id = socket.Operations.Last(op => op.Op == "request").Id;
            client.GetType().GetMethod("OnError", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client,
                new object?[] { new object?[] { id, code, "offline recovery test" } });
        }
        var time = DateTime.UtcNow;
        for (var i = 1; i <= 3; i++)
        {
            Error(100); time = time.AddMinutes(2);
            await client.RepairSubscriptionsAsync(time, 84, default);
            Check(socket.Operations.Count(op => op.Op == "request") == i + 1, "Transient error can recover within bounded attempts");
        }
        Error(100); await client.RepairSubscriptionsAsync(time.AddHours(1), 84, default);
        Check(socket.Operations.Count(op => op.Op == "request") == 4, "No infinite retry storm");
        using var other = await client.SubscribeAsync([contract with { ConId = 1000 }], new(true, true, false), _ => { }, 84, default);
        Error(354); var before = socket.Operations.Count(op => op.Op == "request");
        await client.RepairSubscriptionsAsync(time.AddHours(2), 84, default);
        Check(socket.Operations.Count(op => op.Op == "request") == before, "Permissions are not retried automatically");
    }
}
