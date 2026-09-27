using System.Globalization;
using System.Text.Json;
using IbOptionFlowProbe;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe.Tests;

internal static class AnalysisTests
{
    private static void Check(bool ok, string message = "analysis assertion failed") { if (!ok) throw new Exception(message); }
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("analysis exact match, inside, outside, locked and crossed quotes", () =>
        {
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 20, 20, 100).Category == "AT_ASK");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 1, 20, 20, 100).Category == "AT_BID");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 1.5m, 20, 20, 100).Category == "INSIDE_SPREAD");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 3, 20, 20, 100).Category == "OUTSIDE_QUOTE");
            foreach (var (bid, ask) in new[] { (1m, 1m), (2m, 1m), (0m, 1m) })
                Check(ProbeAnalyzer.MatchForTest(bid, ask, 1, 20, 20, 100).Category == "UNKNOWN");
            return Task.CompletedTask;
        });
        await test("analysis independent side ages and four thresholds", () =>
        {
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 200, 5, 100).Category == "UNKNOWN");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 200, 5, 250).Category == "AT_ASK");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 501, 5, 500).Category == "UNKNOWN");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 501, 5, 1000).Category == "AT_ASK");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, -1, 5, 1000).Category == "UNKNOWN");
            return Task.CompletedTask;
        });
        await test("analysis extended thresholds and unlimited retain quote safety", () =>
        {
            foreach (var age in new[] { 2000, 5000 })
            {
                Check(ProbeAnalyzer.MatchForTest(1, 2, 2, age, age, age).Category == "AT_ASK");
                Check(ProbeAnalyzer.MatchForTest(1, 2, 2, age + 1, 1, age).Reason == "QUOTE_TOO_OLD");
            }
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 60000, 60000, -1).Category == "AT_ASK");
            Check(ProbeAnalyzer.MatchForTest(1, 1, 1, 60000, 60000, -1).Category == "UNKNOWN");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, 60000, 60000, -1, 3).Category == "UNKNOWN");
            Check(ProbeAnalyzer.MatchForTest(1, 2, 2, -1, 1, -1).Category == "UNKNOWN");
            var f = new Fixture(); f.Ready(); f.Trade(0, 100);
            Check(f.Trade(60000, 101).Matches.Single(x => x.MaximumAgeMs == -1).Category == "AT_BID");
            f.Quote(60001, 1, -1);
            Check(f.Trade(60002, 102).Matches.All(x => x.Category == "UNKNOWN"));
            f.Emit(60003, "connectionClosed"); f.Type(); f.Trade(60004, 103);
            Check(f.Trade(60005, 104).Matches.Single(x => x.MaximumAgeMs == -1).Reason == "MISSING_QUOTE");
            return Task.CompletedTask;
        });
        await test("analysis late quote never changes a previous decision", () =>
        {
            var f = new Fixture(); f.Type(); f.Trade(0, 100); var d = f.Trade(50, 101);
            f.Quote(51, 1, 1); f.Quote(51, 2, 2);
            Check(d.Matches.All(x => x.Category == "UNKNOWN") && d.BidSequence == null && d.AskSequence == null);
            Check(f.Trade(60, 102).Matches.All(x => x.Category == "AT_BID"));
            return Task.CompletedTask;
        });
        await test("analysis first sample baseline and exact premium multiplier", () =>
        {
            var f = new Fixture(); f.Ready(); Check(f.Trade(0, 100).Disposition == "BASELINE");
            Check(f.Total().ObservedVolume == 0 && f.Total().VolumeCoverage == null);
            f.Trade(50, 102, size: 2);
            var row = f.Total(); Check(row.ObservedVolume == 2 && row.ObservedPremium == 200);
            Check(row.CumulativeVolumeChange == 2 && row.CumulativePremiumChange == 200 && row.VolumeCoverage == 1);
            return Task.CompletedTask;
        });
        await test("analysis duplicates and distinct same-millisecond trades", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); var a = f.Trade(50, 101);
            var repeated = f.Trade(50, 101); var b = f.Trade(50, 103, size: 2);
            Check(repeated.Disposition == "DUPLICATE" && a.Disposition == "ACCEPTED" && b.Disposition == "ACCEPTED");
            Check(f.Total().ObservedVolume == 3);
            return Task.CompletedTask;
        });
        await test("analysis unchanged print with changed cumulative counter stays ambiguous", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Trade(50, 101);
            Check(f.Trade(50, 102).Disposition == "AMBIGUOUS_SAME_PRINT");
            Check(f.Total().ObservedVolume == 1 && f.Total().CumulativeVolumeChange == 2);
            return Task.CompletedTask;
        });
        await test("analysis missing prints never inherit last side and overshoot not capped", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Trade(50, 110, size: 2);
            Check(f.Total().VolumeCoverage == .2m && f.Total().UnexplainedVolume == 8);
            Check(f.Total().Scenarios[0].Categories["AT_BID"].Volume == 2);
            var g = new Fixture(); g.Ready(); g.Trade(0, 100); g.Trade(50, 101, size: 2);
            Check(g.Total().VolumeCoverage == 2 && g.Total().OverReconciled);
            return Task.CompletedTask;
        });
        await test("analysis resets revisions and out-of-order events excluded", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Trade(100, 101);
            Check(f.Trade(150, 99).Disposition == "COUNTER_RESET");
            Check(f.Trade(200, 100).Disposition == "ACCEPTED");
            Check(f.Trade(190, 101).Disposition == "OUT_OF_ORDER");
            Check(f.Trade(220, 100).Disposition == "REVISION_OR_REPEAT");
            Check(f.Total().ObservedVolume == 2 && f.Total().CumulativeVolumeChange == 2);
            return Task.CompletedTask;
        });
        await test("analysis parse failure breaks cumulative baseline", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100);
            f.Emit(20, "tickString", 77, "broken");
            Check(f.Trade(30, 110).Disposition == "BASELINE" && f.Total().ObservedVolume == 0);
            return Task.CompletedTask;
        });
        await test("analysis scopes rights and contracts isolated", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100, field: 77); f.Trade(10, 500, field: 48);
            f.Trade(50, 102, size: 2, field: 77); f.Trade(60, 505, size: 5, field: 48);
            Check(f.Total("RegularTrades").ObservedVolume == 2 && f.Total("AllTimeAndSales").ObservedVolume == 5);
            f.Contract = f.Contract with { ConId = 2, Right = OptionRight.Put }; f.Ready();
            Check(f.Trade(70, 1000).Disposition == "BASELINE");
            Check(f.Engine.Rows(0).Length == 3);
            return Task.CompletedTask;
        });
        await test("analysis quote size does not refresh price time", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Emit(1900, "tickSize", 0, "20");
            f.Emit(1900, "tickSize", 3, "20"); var d = f.Trade(1950, 101);
            Check(d.Matches.Where(x => x.MaximumAgeMs <= 1000 && x.MaximumAgeMs > 0).All(x => x.Reason == "QUOTE_TOO_OLD") && d.BidSizeSequence != null);
            Check(d.Matches.Where(x => x.MaximumAgeMs > 1000 || x.MaximumAgeMs == -1).All(x => x.Category == "AT_BID"));
            return Task.CompletedTask;
        });
        await test("analysis future source time preserved and trace references quotes", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100);
            var d = f.Trade(150, 101, received: 100);
            Check(d.SourceLagMs == -50 && f.Total().FutureSourceEvents == 1);
            Check(d.BidSequence == 2 && d.AskSequence == 3 && d.Matches.Length == 7);
            return Task.CompletedTask;
        });
        await test("analysis delayed and unconfirmed data never get realtime direction", () =>
        {
            var f = new Fixture(); f.Type(3); f.Quote(0, 1, 1); f.Quote(0, 2, 2); f.Trade(0, 100);
            Check(f.Trade(50, 101).Matches.All(x => x.Category == "UNKNOWN") && f.Total().DelayedEvents == 2);
            Check(ProbeAnalyzer.MatchForTest(1, 2, 1, 1, 1, 100, 0).Category == "UNKNOWN");
            return Task.CompletedTask;
        });
        await test("analysis disconnect and connection epochs establish new baseline", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Trade(50, 101);
            f.Emit(60, "connectionClosed"); f.Ready(); Check(f.Trade(100, 200).Disposition == "BASELINE");
            f.Epoch = 2; Check(f.Trade(110, 201).Disposition == "BASELINE");
            Check(f.Total().ObservedVolume == 1 && f.Engine.Boundaries == 2);
            return Task.CompletedTask;
        });
        await test("analysis fixed 1 3 5 10 intervals and straddling deltas", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(59000, 100); f.Trade(61000, 110);
            Check(f.Engine.Rows().Select(x => x.IntervalMinutes).Distinct().Order().SequenceEqual(new[] { 0, 1, 3, 5, 10 }));
            var bucket = f.Engine.Rows(1).Last();
            Check(bucket.BucketStartUtc == f.Start.AddMinutes(1) && bucket.ComparisonPairs == 0 && bucket.BoundaryCrossings == 1);
            Check(f.Total().CumulativeVolumeChange == 10);
            return Task.CompletedTask;
        });
        await test("analysis interval suffix and startup partial flags", () =>
        {
            var f = new Fixture(observationOffset: 30000, endOffset: 70000); f.Ready(); f.Trade(40000, 100); f.Trade(61000, 101);
            Check(f.Engine.Rows(1).All(x => x.Partial));
            return Task.CompletedTask;
        });
        await test("analysis session gaps do not bridge and SPX GTH is distinct", () =>
        {
            var f = new Fixture(); f.Contract = f.Contract with { Ticker = "SPX", TradingClass = "SPXW", TradingHours = "20260916:0800-20260916:0900,20260916:0930-20260916:1600" };
            f.Ready(); f.Trade(-5400000, 100); Check(f.Engine.Rows(0).Single().Session == "GTH");
            Check(f.Trade(-1200000, 200).Disposition == "OUTSIDE_SESSION");
            Check(f.Trade(0, 300).Disposition == "BASELINE");
            Check(f.Engine.Rows(0).Length == 2 && f.Engine.Rows(0).All(x => x.ObservedVolume == 0));
            return Task.CompletedTask;
        });
        await test("analysis no events and zero denominator remain unknown", () =>
        {
            var f = new Fixture(); Check(f.Engine.Rows().Length == 0); f.Ready(); f.Trade(0, 100); f.Trade(50, 100);
            Check(f.Total().VolumeCoverage == null && f.Total().Scenarios.All(x => x.Tendency == null));
            return Task.CompletedTask;
        });
        await test("analysis quiet buckets explicitly unknown and short session boundary respected", () =>
        {
            var f = new Fixture();
            f.Contract = f.Contract with { TradingHours = "20260916:0930-20260916:0940" };
            f.Engine.AddMissingRows([f.Contract]);
            Check(f.Engine.Rows(1).Length == 20 && f.Engine.Rows(1).All(x => x.ObservedFromUtc == null && x.VolumeCoverage == null));
            f.Ready(); Check(f.Trade(600000, 100).Disposition == "OUTSIDE_SESSION");
            return Task.CompletedTask;
        });
        await test("analysis shared IB calendar summer winter and real half-day hours", () =>
        {
            var summer = IbTradingHoursParser.ParseEastern("20260916:0930-20260916:1600", false).Single();
            var winter = IbTradingHoursParser.ParseEastern("20261201:0930-20261201:1600", false).Single();
            var half = IbTradingHoursParser.ParseEastern("20261127:0930-20261127:1300", false).Single();
            Check(summer.StartUtc.Hour == 13 && winter.StartUtc.Hour == 14 && half.EndUtc.Hour == 18);
            Check(!half.Contains(half.EndUtc));
            return Task.CompletedTask;
        });
        await test("analysis age uses monotonic time and negative quotes invalidate", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(0, 100); f.Quote(10, 1, -1);
            Check(f.Trade(20, 101).Matches.All(x => x.Category == "UNKNOWN"));
            f.Quote(30, 1, 1);
            Check(f.Trade(40, 102).BidAgeMs == 10);
            return Task.CompletedTask;
        });
        await test("robust report exposes versioned alignment and bucket evidence", async () =>
        {
            var dir = await ProbeDemo.CreateAsync(root);
            var result = await AnalysisRunner.RunAsync(dir);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Directory, "report.json")));
            Check(json.RootElement.GetProperty("schema_version").GetInt32() == 3, "New evidence schema must be version 3");
            Check(json.RootElement.GetProperty("robustness").GetProperty("buckets").GetArrayLength() > 0);
            Check(json.RootElement.GetProperty("robustness").GetProperty("settings").GetProperty("scenarios").GetArrayLength() == 36);
            var html = await File.ReadAllTextAsync(Path.Combine(result.Directory, "report.html"));
            Check(html.Contains("抗误判余量") && html.Contains("robust-method") && html.Contains("robust-page"));
        });
        await test("analysis file replay deterministic and HTML safely encoded", async () =>
        {
            var dir = await ProbeDemo.CreateAsync(root);
            var a = await AnalysisRunner.RunAsync(dir); var b = await AnalysisRunner.RunAsync(dir);
            Check(await File.ReadAllTextAsync(Path.Combine(a.Directory, "report.json")) == await File.ReadAllTextAsync(Path.Combine(b.Directory, "report.json")));
            Check(a.Report.Synthetic && a.Report.Totals.Length == 8 && !a.Report.Integrity.AbnormalEnd);
            Check(Directory.GetFiles(a.Directory, "decisions-*.jsonl").Length > 0);
            var html = ProbeHtml.Render(a.Report with { Warnings = ["<script>bad()</script>"] });
            Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>"));
        });
        await test("analysis supports 20 contracts end to end and still rejects duplicate identities", async () =>
        {
            var dir = await ProbeDemo.CreateAsync(root);
            var path = Path.Combine(dir, "contracts.json");
            var contracts = JsonSerializer.Deserialize<ProbeContract[]>(await File.ReadAllTextAsync(path), ProbeJson.Options)!;
            var extended = contracts.Concat(Enumerable.Range(0, 20 - contracts.Length).Select(i => contracts[0] with
            { Contract = contracts[0].Contract with { ConId = 900000 + i, StrikeUsd = 200 + i } })).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(extended, ProbeJson.Options));
            var result = await AnalysisRunner.RunAsync(dir);
            Check(result.Report.Totals.Select(x => x.Contract.ConId).Distinct().Count() == 20);
            Check(File.Exists(Path.Combine(result.Directory, "report.html")));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(extended.Append(extended[0]), ProbeJson.Options));
            try { await AnalysisRunner.RunAsync(dir); throw new Exception("duplicate accepted"); }
            catch (ArgumentException e) { Check(e.Message == "合约清单无效。"); }
        });
        await test("analysis missing raw records and corruption break observation", async () =>
        {
            var dir = await ProbeDemo.CreateAsync(root);
            var file = Directory.GetFiles(dir, "events-*.jsonl").First();
            var lines = await File.ReadAllLinesAsync(file);
            lines[20] = "{broken";
            await File.WriteAllLinesAsync(file, lines.Where((_, i) => i != 30));
            var result = await AnalysisRunner.RunAsync(dir);
            Check(result.Report.Integrity.AbnormalEnd && result.Report.Boundaries > 0
                && result.Report.Conclusion == "UNSUITABLE_FOR_FULL_FLOW_INFERENCE");
        });
    }
    private sealed class Fixture
    {
        public readonly DateTime Start = new(2026, 9, 16, 13, 30, 0, DateTimeKind.Utc);
        public OptionContractDescriptor Contract = new(1, "QQQ", new DateOnly(2026, 9, 16), 100,
            OptionRight.Call, "QQQ", "SMART", 100, "20260916:0930-20260916:1600");
        public ProbeAnalyzer Engine;
        private long _sequence;
        private readonly Guid _run = Guid.NewGuid();
        public int Epoch = 1;
        public Fixture(int observationOffset = 0, int endOffset = 1800000)
        { Engine = new(1000, Start.AddMilliseconds(observationOffset), Start.AddMilliseconds(endOffset)); }
        public void Type(int type = 1) => Emit(0, "marketDataType", dataType: type);
        public void Ready() { Type(); Quote(0, 1, 1); Quote(0, 2, 2); }
        public void Quote(long ms, int field, decimal value) => Emit(ms, "tickPrice", field, value.ToString(CultureInfo.InvariantCulture));
        public TradeDecision Trade(long ms, decimal volume, decimal size = 1, int field = 77, long? received = null)
            => Emit(received ?? ms, "tickString", field, FormattableString.Invariant($"1;{size};{new DateTimeOffset(Start.AddMilliseconds(ms)).ToUnixTimeMilliseconds()};{volume};1;true"))!;
        public TradeDecision? Emit(long ms, string kind, int? field = null, string? value = null, int? dataType = null)
        {
            var raw = new IbRawMarketEvent(++_sequence, Start.AddMilliseconds(ms), ms, kind, (int)Contract.ConId,
                Contract, field, value, MarketDataType: dataType);
            return Engine.Process(RawEventParser.Convert(_run, raw) with { ConnectionEpoch = Epoch });
        }
        public AnalysisRow Total(string scope = "RegularTrades") => Engine.Rows(0).Single(x => x.Scope == scope && x.Contract.ConId == Contract.ConId);
    }
}
