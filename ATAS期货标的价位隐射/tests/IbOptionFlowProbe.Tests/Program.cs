using System.Diagnostics;
using System.Text.Json;
using IbOptionFlowProbe;
using IbOptionFlowProbe.Tests;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

var root = Path.Combine(Path.GetTempPath(), "ib-flow-probe-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Check(bool condition, string text = "assertion failed") { if (!condition) throw new Exception(text); }
string Folder() { var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
IbRawMarketEvent Raw(long sequence = 1, string value = "1.25;2;1789560000123;100;1.20;true", int field = 77)
    => new(sequence, new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc), sequence, "tickString", 4, null, field, value);
async Task Test(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
try
{
    await Test("embedded official API without ATAS or socket", async () =>
    {
        await using var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        Check(client.IsAvailable && !client.IsConnected);
        Check(!typeof(ProbeConfig).Assembly.GetReferencedAssemblies().Any(a => a.Name?.StartsWith("ATAS.") == true));
    });
    await Test("disabled raw observer performs no payload conversion", async () =>
    {
        await using var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        var bomb = new ThrowingString();
        client.ObserveRawCallback("tickPrice", [1, 1, bomb]);
        Check(bomb.Reads == 0 && client.RawObservationFailures == 0);
    });
    await Test("raw callback order quote flags and privacy whitelist", async () =>
    {
        await using var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        var events = new List<IbRawMarketEvent>(); client.SetRawObserver(events.Add);
        client.ObserveRawCallback("tickPrice", [4, 1, 1.2, new Attributes()]);
        client.ObserveRawCallback("tickSize", [4, 0, 12m]);
        client.ObserveRawCallback("tickString", [4, 77, "1;2;1789560000123;5;1;false"]);
        client.ObserveRawCallback("marketDataType", [4, 3]);
        client.ObserveRawCallback("managedAccounts", ["SECRET_ACCOUNT"]);
        client.ObserveRawCallback("error", [4, 0L, 354, "SECRET_ACCOUNT", "PRIVATE"]);
        Check(events.Count == 5 && events.Select(x => x.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }));
        Check(events[0].CanAutoExecute == true && events[0].PreOpen == true);
        Check(events[3].MarketDataType == 3 && events[4].ErrorCode == 354);
        Check(!JsonSerializer.Serialize(events).Contains("SECRET"));
    });
    await Test("concurrent raw publication sequence remains ordered", async () =>
    {
        await using var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        var sequence = new List<long>();
        client.SetRawObserver(item => sequence.Add(item.Sequence));
        Parallel.For(0, 1000, _ => client.ObserveRawCallback("tickPrice", [4, 1, 1.25]));
        Check(sequence.SequenceEqual(Enumerable.Range(1, 1000).Select(x => (long)x)));
    });
    await Test("observer exception is isolated and counted", async () =>
    {
        await using var client = new ReflectionIbOptionGatewayClient(new("127.0.0.1", 4001, 2290));
        client.SetRawObserver(_ => throw new IOException());
        client.ObserveRawCallback("connectionClosed", []);
        Check(client.RawObservationFailures == 1);
    });
    await Test("six RT fields future and same millisecond retained", () =>
    {
        var run = Guid.NewGuid();
        var a = RawEventParser.Convert(run, Raw());
        var b = RawEventParser.Convert(run, Raw(2, "1.30;3;1789560000123;103;1.21;false", 48));
        Check(a.Trade?.SourceUnixMilliseconds == b.Trade?.SourceUnixMilliseconds);
        Check(a.Trade?.SingleMarketMaker == true && b.Trade?.SingleMarketMaker == false);
        Check(a.Trade?.Size == 2 && b.Trade?.Size == 3 && a.Raw.Field == 77 && b.Raw.Field == 48);
        Check(a.Trade!.SourceUtc > a.Raw.ReceivedUtc && a.ParseError == null);
        return Task.CompletedTask;
    });
    await Test("invalid RT retained with parse error", () =>
    {
        foreach (var text in new[] { "NaN;1;1;1;1;true", "1;1;999999999999999;1;1;true", "bad", "1;1;1;-2;1;true" })
        {
            var parsed = RawEventParser.Convert(Guid.NewGuid(), Raw(value: text));
            Check(parsed.ParseError != null && parsed.Raw.Value == text && parsed.Trade == null);
        }
        return Task.CompletedTask;
    });
    await Test("ATM tie lower and symmetric actual strike ladder", () =>
    {
        Check(ProbeContracts.SelectThree(new decimal[] { 98, 99, 100, 101, 102 }, 100.5m).SequenceEqual(new decimal[] { 99, 100, 101 }));
        Check(ProbeContracts.SelectThree(new decimal[] { 99, 100 }, 100).Length == 0);
        return Task.CompletedTask;
    });
    await Test("configurable symmetric ladder and legacy config defaults", () =>
    {
        Check(ProbeContracts.SelectSymmetric([90, 95, 100, 105, 110], 102.5m, 2).SequenceEqual(new decimal[] {90,95,100,105,110}));
        Check(ProbeContracts.SelectSymmetric([90, 100, 110], 91, 1).Length == 0);
        Check(ProbeContracts.SelectSymmetric([100], 102, 0).SequenceEqual(new decimal[] {100}));
        var legacy = JsonSerializer.Deserialize<ProbeConfig>("{\"reference_spots\":{\"QQQ\":100}}", ProbeJson.Options)!;
        legacy.Validate(); Check(legacy.StrikesEachSide == 1 && legacy.RequiredMarketDataLines == 6);
        var multi = legacy with { StrikesEachSide = 3, ReferenceSpots = new() { ["QQQ"] = 100, ["SPX"] = 7000 } };
        multi.Validate(); Check(multi.RequiredMarketDataLines == 28);
        foreach (var n in new[] {-1,21,int.MaxValue})
        { try { (legacy with { StrikesEachSide=n }).Validate(); throw new Exception("invalid accepted"); } catch (ArgumentException) {} }
        return Task.CompletedTask;
    });
    await Test("custom range reaches subscription above old 12-line cap and preserves manifest", async () =>
    {
        foreach (var n in new[] {0,3,20})
        {
            var fake = new FakeGateway { CloseAfterSubscribe=false, Strikes=Enumerable.Range(50,101).Select(x=>(decimal)x).ToArray() };
            var config = new ProbeConfig { StrikesEachSide=n, OutputDirectory=Folder(), ReferenceSpots=new() { ["QQQ"]=100 } };
            var result = await CaptureRunner.RunAsync(config, default,
                () => new(fake, o => fake.Observer=o, () => 0, () => false),
                () => new DateTime(2026,9,16,15,0,0,DateTimeKind.Utc), TimeSpan.Zero);
            Check(result.Summary.Complete && fake.LastBudget==(2*n+1)*2 && fake.LastContractCount==fake.LastBudget);
            var saved=JsonSerializer.Deserialize<ProbeManifest>(await File.ReadAllTextAsync(Path.Combine(result.Directory,"manifest.json")),ProbeJson.Options)!;
            Check(saved.Config.StrikesEachSide==n);
        }
        try { await ProbeContracts.DiscoverAsync(new FakeGateway(),OptionUnderlyingProfile.Qqq,100,
            new DateTime(2026,9,16,15,0,0,DateTimeKind.Utc),default,3); throw new Exception("silently shrank"); }
        catch (IbOptionGatewayException e) { Check(e.Code=="NO_CONTRACTS"); }
    });
    await Test("QQQ closed and SPX GTH next session expiration", () =>
    {
        var night = new DateTime(2026, 9, 17, 1, 0, 0, DateTimeKind.Utc);
        Check(ProbeContracts.Expiration(OptionUnderlyingProfile.Spx, night) == new DateOnly(2026, 9, 17));
        try { ProbeContracts.Expiration(OptionUnderlyingProfile.Qqq, night); throw new Exception("closed accepted"); }
        catch (IbOptionGatewayException e) { Check(e.Code == "CLOSED"); }
        return Task.CompletedTask;
    });
    await Test("candidate-only nonexistent strike not selected", async () =>
    {
        var contracts = await ProbeContracts.DiscoverAsync(new FakeGateway(), OptionUnderlyingProfile.Qqq,
            100, new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc), default);
        Check(contracts.Length == 6 && contracts.All(x => x.Contract.StrikeUsd != 100.5m));
        Check(contracts.Select(x => x.Contract.StrikeUsd).Distinct().SequenceEqual(new decimal[] { 100, 99, 101 }));
    });
    await Test("writer lossless rotation and idempotent dispose", async () =>
    {
        var dir = Folder(); var writer = new RawCaptureWriter(dir, Guid.NewGuid(), rotateBytes: 1000);
        for (var i = 0; i < 100; i++) writer.Publish(Raw(i + 1));
        await writer.DisposeAsync(); await writer.DisposeAsync();
        Check(writer.Written == 100 && writer.Accepted == 100 && writer.Dropped == 0 && writer.Failure == null);
        Check(Directory.GetFiles(dir).Length > 1);
        var inspection = await CaptureInspector.InspectAsync(dir);
        Check(inspection.Records == 100 && inspection.InvalidRecords == 0 && inspection.AbnormalEnd);
    });
    await Test("bounded queue overflow is counted", async () =>
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var writer = new RawCaptureWriter(Folder(), Guid.NewGuid(), capacity: 2, open: _ =>
        { entered.Set(); if (!release.Wait(5000)) throw new IOException(); return new MemoryStream(); });
        writer.Publish(Raw()); Check(entered.Wait(5000));
        for (var i = 0; i < 100; i++) writer.Publish(Raw(i + 2));
        release.Set(); await writer.DisposeAsync();
        Check(writer.Dropped > 0 && writer.Written == writer.Accepted && writer.QueuePeak <= 3);
    });
    await Test("disk failure and size limit signal stop", async () =>
    {
        var failure = new RawCaptureWriter(Folder(), Guid.NewGuid(), open: _ => throw new IOException());
        failure.Publish(Raw()); Check(await failure.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "IO_FAILURE");
        await failure.DisposeAsync(); Check(failure.Failure != null && failure.Written == 0);
        var limit = new RawCaptureWriter(Folder(), Guid.NewGuid(), maxBytes: 1);
        limit.Publish(Raw()); Check(await limit.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "SIZE_LIMIT");
        await limit.DisposeAsync(); Check(limit.Bytes == 0 && limit.Dropped == 1);
    });
    await Test("truncated tail recoverable interior corruption visible", async () =>
    {
        var dir = Folder(); var path = Path.Combine(dir, "events-00001.jsonl");
        var valid = JsonSerializer.Serialize(RawEventParser.Convert(Guid.NewGuid(), Raw()), ProbeJson.Options);
        var second = JsonSerializer.Serialize(RawEventParser.Convert(JsonSerializer.Deserialize<ProbeEvent>(valid, ProbeJson.Options)!.RunId, Raw(2)), ProbeJson.Options);
        await File.WriteAllTextAsync(path, valid + "\nBAD\n" + second + "\n{\"schema_");
        var inspection = await CaptureInspector.InspectAsync(dir);
        Check(inspection.Records == 2 && inspection.InvalidRecords == 1 && inspection.TruncatedTail && inspection.AbnormalEnd);
    });
    await Test("fatal error categories separate from expected discovery misses", () =>
    {
        foreach (var code in new[] { 326, 101, 354, 10089, 10186, 1100 }) Check(CaptureRunner.FatalError(Raw() with { ErrorCode = code }) != null);
        Check(CaptureRunner.FatalError(Raw() with { ErrorCode = 10090 }) == null);
        Check(CaptureRunner.FatalError(Raw() with { ErrorCode = 200 }) == null);
        return Task.CompletedTask;
    });
    await Test("capture disconnect closes lease and connection", async () =>
    {
        var fake = new FakeGateway();
        var config = new ProbeConfig { OutputDirectory = Folder(), ReferenceSpots = new() { ["QQQ"] = 100 } };
        var result = await CaptureRunner.RunAsync(config, default,
            () => new(fake, observer => fake.Observer = observer, () => 0, () => false),
            () => new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc));
        Check(result.Summary.StopReason == "CONNECTION_CLOSED" && !result.Summary.Complete);
        Check(fake.Disposed && fake.LeaseDisposed && fake.ActiveMarketDataLines == 0 && fake.Observer == null);
        Check((await CaptureInspector.InspectAsync(result.Directory)).Records == 2);
    });
    await Test("connect failures cancellation and no contracts release resources", async () =>
    {
        foreach (var error in new[] { "CLIENT_ID_IN_USE", "NO_PERMISSION", "LINE_LIMIT", "CONNECT_TIMEOUT", "NO_CONTRACTS", "CANCELLED" })
        {
            var fake = new FakeGateway { ConnectError = error is "NO_CONTRACTS" or "CANCELLED" ? null : error, MissingContracts = error == "NO_CONTRACTS" };
            using var cancel = new CancellationTokenSource(); if (error == "CANCELLED") cancel.Cancel();
            var result = await CaptureRunner.RunAsync(new ProbeConfig { OutputDirectory = Folder(), ReferenceSpots = new() { ["QQQ"] = 100 } }, cancel.Token,
                () => new(fake, observer => fake.Observer = observer, () => 0, () => false),
                () => new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc));
            Check(fake.Disposed && !result.Summary.Complete && result.Summary.StopReason == error, error);
        }
    });
    await Test("normal timed completion drains records and releases resources", async () =>
    {
        var fake = new FakeGateway { CloseAfterSubscribe = false };
        var result = await CaptureRunner.RunAsync(new ProbeConfig { OutputDirectory = Folder(), ReferenceSpots = new() { ["QQQ"] = 100 } }, default,
            () => new(fake, observer => fake.Observer = observer, () => 0, () => false),
            () => new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        Check(result.Summary.Complete && result.Summary.Written == 1 && fake.Disposed && fake.LeaseDisposed);
        Check(!(await CaptureInspector.InspectAsync(result.Directory)).AbnormalEnd);
    });
    await Test("partial permission preserves subsequent callbacks and warns in report", async () =>
    {
        var fake = new FakeGateway { CloseAfterSubscribe = false, PartialPermissionWarning = true };
        var result = await CaptureRunner.RunAsync(new ProbeConfig { OutputDirectory = Folder(), ReferenceSpots = new() { ["QQQ"] = 100 } }, default,
            () => new(fake, observer => fake.Observer = observer, () => 0, () => false),
            () => new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
        Check(result.Summary.Complete && result.Summary.Written == 3 && fake.Disposed && fake.LeaseDisposed);
        var analysis = await AnalysisRunner.RunAsync(result.Directory);
        Check(analysis.Report.Diagnostics["IB_10090"] == 1);
        Check(analysis.Report.TradeCallbacks == 2);
        Check(analysis.Report.Warnings.Any(x => x.Contains("10090")));
        Check(ProbeHtml.Render(analysis.Report).Contains("10090"));
    });
    await Test("invalid config does not allow Client ID zero or default spots", () =>
    {
        foreach (var c in new[] { new ProbeConfig(), new ProbeConfig { ClientId = 0, ReferenceSpots = new() { ["QQQ"] = 100 } },
            new ProbeConfig { ReferenceSpots = new() { ["QQQ"] = 0 } } })
        { try { c.Validate(); throw new Exception("invalid accepted"); } catch (ArgumentException) { } }
        return Task.CompletedTask;
    });
    await DirectionMetricsTests.RunAsync(Test);
    await DirectionAnalysisTests.RunAsync(Test);
    await DirectionHtmlTests.RunAsync(Test);
    await AnalysisTests.RunAsync(Test, root);
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Probe offline: {passed} passed, {failed} failed; no live Gateway connection.");
return failed == 0 ? 0 : 1;

sealed class ThrowingString
{
    public int Reads;
    public override string ToString() { Reads++; throw new InvalidOperationException(); }
}
sealed class Attributes
{
    public bool CanAutoExecute => true;
    public bool PastLimit => false;
    public bool PreOpen => true;
}
