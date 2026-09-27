using System.Globalization;
using IbOptionFlowProbe;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe.Tests;

internal static class DirectionAnalysisTests
{
    private static void Check(bool ok, string why = "direction assertion failed") { if (!ok) throw new Exception(why); }
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Mid categories conserve premium without entering unknown reasons", () =>
        {
            var a = new DirectionAmounts(); a.Add(0,20,"LOCKED_QUOTE"); a.Add(0,10,"MIDPOINT_UNKNOWN");
            a.Add(1,60,"AT_ASK"); a.Add(-1,30,"AT_BID"); a.Add(0,10,"OUTSIDE_QUOTE");
            Check(a.Mid==30 && a.Unknown==10 && a.UnknownPremiums.Count==1);
            Check(a.Metrics.Total==130 && a.Metrics.Coverage==120m/130 && a.Metrics.Lower==20m/130);
            Check(!UnknownReasons.All.Any(x=>x.Code is "U11" or "U17"));
            var f=new Fixture(); f.Ready(); f.Trade(100,100,100,1.5m); f.Trade(1000,1300,101,1.5m);
            var row=f.Finish().Totals.Single();
            Check(row.Consensus["Midpoint"].Mid==150 && row.Consensus["Midpoint"].Unknown==0);
            return Task.CompletedTask;
        });
        await test("U15 switch reclassifies each candidate but preserves stale quote checks", () =>
        {
            foreach(var enabled in new[]{false,true})
            {
                var f=new Fixture(enabled); f.Ready(); f.Trade(100,100,100,3);
                f.Trade(1000,1300,101,3); f.Trade(2000,2300,102,.5m);
                var row=f.Finish().Totals.Single(); var a=row.Scenarios["Latest/Midpoint/-1"];
                Check(enabled ? a.Buy==300 && a.Sell==50 && a.Unknown==0 : a.Unknown==350 && a.Buy==0);
                Check(row.Scenarios["Latest/Midpoint/100"].Unknown==350);
                Check(enabled ? row.Consensus["Midpoint"].Buy==300 && row.Consensus["Midpoint"].Sell==50 : row.Consensus["Midpoint"].Unknown==350);
                Check(a.Metrics.Total==350);
            }
            var locked=new Fixture(true); locked.Ready(); locked.Quote(0,2,1);
            locked.Trade(100,100,100,1); locked.Trade(1000,1300,101,1);
            Check(locked.Finish().Totals.Single().Scenarios["Latest/Midpoint/-1"].Mid==100);
            return Task.CompletedTask;
        });
        await test("Mid versus directional candidate remains explicit U26", () =>
        {
            var f=new Fixture(); f.Ready(); f.Trade(100,100,100,1.5m);
            f.Quote(1050,2,1.8m); var e=f.Trade(1000,1300,101,1.5m);
            Check(e.Consensus.Single(x=>x.ScenarioId=="Midpoint").Reason=="MID_DIRECTION_DISAGREEMENT");
            return Task.CompletedTask;
        });
        await test("history exact order checkpoint capacity and revoked quotes", () =>
        {
            var h = new QuoteHistory(1000, 3, 60);
            var f = new Fixture();
            h.Add(f.Raw(0, "marketDataType", type: 1));
            h.Add(f.Raw(10, "tickPrice", 1, "1")); h.Add(f.Raw(10, "tickPrice", 2, "2"));
            Check(h.At(10, 10).State!.Ask!.Price == 2);
            h.Add(f.Raw(20, "tickPrice", 1, "-1"));
            Check(h.At(5, 20).Error == "HISTORY_UNAVAILABLE");
            Check(h.At(20, 20).State!.Bid!.Invalid);
            Check(h.At(21, 20).Error == "FUTURE_CANDIDATE" && h.Count == 3 && h.Evictions == 1);
            Check(h.At(70000, 70000).State!.Bid!.Invalid && h.Count == 1);
            Check(h.At(20, 70000).Error == "HISTORY_UNAVAILABLE");
            h.Clear(); Check(h.At(70000, 70000).Error == "MISSING_QUOTE_HISTORY");
            return Task.CompletedTask;
        });
        await test("source alignment exposes late trade latest quote sign conflict", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 2);
            f.Quote(800, 1, 2); f.Quote(800, 2, 3);
            var d = f.Trade(500, 1000, 101, 2);
            Check(d.Matches.Single(x => x.ScenarioId == "Latest/Midpoint/-1").Sign == -1);
            Check(d.Matches.Single(x => x.ScenarioId == "Source0/Midpoint").Sign == 1);
            Check(d.StableSign == 0 && d.StableReason == "DIRECTION_CONFLICT");
            Check(d.Alignments.Single(x => x.Id == "Source0").Quote!.Ask!.Sequence < d.Sequence);
            return Task.CompletedTask;
        });
        await test("future candidates excluded no lookahead and same state is not extra evidence", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            var d = f.Trade(1000, 1100, 101, 1);
            Check(d.StableSign == -1 && d.FeasibleSourceCandidates == 4 && d.DistinctQuoteStates == 1);
            Check(d.Alignments.Single(x => x.Id == "Source+250").Error == "FUTURE_CANDIDATE");
            var before = System.Text.Json.JsonSerializer.Serialize(d, ProbeJson.Options);
            f.Quote(1101, 1, 2);
            Check(before == System.Text.Json.JsonSerializer.Serialize(d, ProbeJson.Options));
            return Task.CompletedTask;
        });
        await test("source ahead cannot create false source consensus", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            var d = f.Trade(1000, 800, 101, 1);
            Check(d.FeasibleSourceCandidates == 1 && d.StableReason == "INSUFFICIENT_TIME_CANDIDATES");
            return Task.CompletedTask;
        });
        await test("midpoint tick supplement stays out of conservative primary", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            var d = f.Trade(1000, 1100, 101, 1.5m);
            Check(d.StableSign == 0 && d.TickSign == 1);
            Check(d.Matches.Single(x => x.ScenarioId == "Latest/Midpoint/-1").Sign == 0);
            Check(d.Matches.Single(x => x.ScenarioId == "Latest/MidpointTick/-1").Reason == "MIDPOINT_TICK");
            Check(f.Trade(2000, 2100, 102, 1.5m).TickSign == 1);
            Check(f.Trade(8000, 8100, 103, 1.5m).TickSign == 0);
            return Task.CompletedTask;
        });
        await test("all scenario rules preserve unknown outside quote and delayed data", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            Check(f.Trade(1000, 1100, 101, 3).Matches.All(x => x.Sign == 0));
            f.Send(1200, "marketDataType", type: 3); f.Quote(1200, 1, 1); f.Quote(1200, 2, 2);
            f.Trade(1300, 1400, 102, 1);
            Check(f.Trade(1500, 1600, 103, 1).Matches.All(x => x.Sign == 0));
            return Task.CompletedTask;
        });
        await test("size updates do not refresh price ages and zero invalidates historical state", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            f.Send(1900, "tickSize", 0, "10"); f.Send(1900, "tickSize", 3, "10");
            var d = f.Trade(1900, 2000, 101, 1);
            Check(d.Matches.Single(x => x.ScenarioId == "Latest/AtQuote/1000").Reason == "QUOTE_TOO_OLD");
            Check(d.Alignments[0].BidAgeMs == 2000);
            f.Send(2100, "tickSize", 0, "0");
            Check(f.Trade(2200, 2300, 102, 1).Matches.Single(x => x.ScenarioId == "Latest/AtQuote/-1").Reason == "INVALID_QUOTE_SIZE");
            return Task.CompletedTask;
        });
        await test("clock jump clears source history and marks evidence", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            f.ClockOffset = 300;
            Check(f.Trade(1000, 1100, 101, 1).StableSign == 0);
            var r = f.Finish(); Check(r.Diagnostics["CLOCK_JUMP"] == 1 && r.Totals[0].Quality.Contains("CLOCK_JUMP"));
            return Task.CompletedTask;
        });
        await test("minute buckets retain history and aggregate premium without duplication", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            for (var i = 0; i < 35; i++) f.Trade(61000 + i * 100, 61100 + i * 100, 101 + i, 1);
            var report = f.Finish(); var r = report.Buckets.Single(x => x.IntervalMinutes == 1 && x.StartUtc == f.Start.AddMinutes(1));
            Check(r.Events == 35 && r.Primary.Sell == 3500 && r.Primary.Metrics.Total == 3500);
            Check(r.Scenarios.Count == 36 && r.Scenarios.Values.All(x => x.Metrics.Total == 3500));
            Check(r.Status == "SELL" && r.HighestPassedPressure == .05m);
            Check(r.LargestPremiums.Sum() == 500 && r.Quality.Contains("RECONCILIATION_BOUNDARY"));
            return Task.CompletedTask;
        });
        await test("raw gap disconnect and scope isolation reset evidence not old results", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            f.Trade(1000, 1100, 101, 1);
            var trade = f.Raw(1200, "tickString", 48, f.Payload(1200, 200, 1)); f.Apply(trade);
            Check(f.Finish().Totals.Count(x => x.Events > 0) == 1);
            f.Send(1300, "connectionClosed");
            Check(f.Trade(1400, 1500, 102, 1).StableSign == 0);
            Check(f.Finish().Totals.First().Quality.Contains("OBSERVATION_GAP"));
            return Task.CompletedTask;
        });
        await test("repeated realtime type preserves quotes but type change resets history", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            f.Send(1000, "marketDataType", type: 1);
            Check(f.Trade(1100, 1200, 101, 1).Disposition == "BASELINE");
            Check(f.Trade(1150, 1250, 102, 1).StableSign == -1);
            f.Send(1300, "marketDataType", type: 2);
            Check(f.Trade(1400, 1500, 102, 1).StableSign == 0);
            f.Send(1600, "marketDataType", type: 1);
            Check(f.Trade(1700, 1800, 103, 1).StableSign == 0);
            return Task.CompletedTask;
        });
        await test("all three methods support the same time consensus and premium conservation", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            var d = f.Trade(1000, 1100, 101, 1.75m);
            Check(d.Consensus.Length == 3 && d.Matches.Length == 36);
            Check(d.Consensus.Single(x => x.ScenarioId == "AtQuote").Sign == 0);
            Check(d.Consensus.Single(x => x.ScenarioId == "Midpoint").Sign == 1);
            Check(d.Consensus.Single(x => x.ScenarioId == "MidpointTick").Sign == 1);
            var row = f.Finish().Totals.Single();
            Check(row.Consensus.Count == 3 && row.Consensus.Values.All(a => a.Metrics.Total == 175));
            foreach (var a in row.Scenarios.Values.Concat(row.Consensus.Values).Append(row.Primary))
                Check(a.UnknownPremiums.Values.Sum() == a.Unknown && a.UnknownCounts.Values.All(v => v > 0));
            return Task.CompletedTask;
        });
        await test("time conflict remains visible when another candidate is unknown", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 2);
            f.Quote(800, 1, 2); f.Quote(800, 2, 3);
            // Source -250 is before the first quote, while source +250 and latest differ.
            var d = f.Trade(200, 1000, 101, 2);
            Check(d.Consensus.Single(x => x.ScenarioId == "Midpoint").Reason == "DIRECTION_CONFLICT");
            Check(f.Finish().Totals.Single().TimeConflictPremiums["Midpoint"] == 200);
            return Task.CompletedTask;
        });
        await test("tick unknown subreasons distinguish same price stale predecessor stale change and interruption", () =>
        {
            static string Reason(DirectionEvidence d) => d.Matches.Single(x => x.ScenarioId == "Latest/MidpointTick/-1").Reason;
            var same = new Fixture(); same.Ready(); same.Trade(100, 100, 100, 1.5m);
            Check(Reason(same.Trade(1000, 1100, 101, 1.5m)) == "TICK_NO_CHANGE");
            Check(Reason(same.Trade(8000, 8100, 102, 1.5m)) == "TICK_PREVIOUS_TOO_OLD");
            var aged = new Fixture(); aged.Ready(); aged.Trade(100, 100, 100, 1);
            aged.Trade(1000, 1100, 101, 1.5m);
            for (var i = 2; i <= 6; i++) aged.Trade(i * 1000, i * 1000 + 100, 100 + i, 1.5m);
            Check(Reason(aged.Trade(7000, 7100, 107, 1.5m)) == "TICK_DIRECTION_TOO_OLD");
            aged.Trade(6500, 7200, 108, 1.5m); // out of source order, removes tick state
            Check(Reason(aged.Trade(7300, 7400, 109, 1.5m)) == "TICK_INTERRUPTED");
            return Task.CompletedTask;
        });
        await test("unknown catalog identifiers colors and observed reason accounting are stable", () =>
        {
            Check(UnknownReasons.All.Select(x => x.Code).Distinct().Count() == UnknownReasons.All.Length);
            Check(UnknownReasons.All.Select(x => x.Color).Distinct().Count() == UnknownReasons.All.Length);
            Check(UnknownReasons.All.All(x => x.Color.StartsWith('#') && x.Example.Length > 5));
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            f.Trade(1000, 1100, 101, 3);
            f.Trade(2000, 2100, 102, 1.5m);
            var keys = UnknownReasons.All.Select(x => x.Key).ToHashSet();
            foreach (var a in f.Finish().Totals.SelectMany(r => r.Scenarios.Values.Concat(r.Consensus.Values)))
            { Check(a.UnknownPremiums.Values.Sum() == a.Unknown); Check(a.UnknownPremiums.Keys.All(keys.Contains)); }
            return Task.CompletedTask;
        });
        await test("U25 groups count each trade once and retain bounded candidate evidence", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 3);
            for (var i = 1; i <= 5; i++) f.Trade(i * 1000, i * 1000 + 100, 100 + i, 3);
            var row = f.Finish().Totals.Single();
            foreach (var a in row.Consensus.Values.Append(row.Primary))
            {
                Check(a.UnresolvedGroups.Values.Sum(g => g.Premium) == a.UnknownPremiums["UNRESOLVED_CANDIDATE"]);
                Check(a.UnresolvedGroups.Values.Sum(g => g.Events) == a.UnknownCounts["UNRESOLVED_CANDIDATE"]);
                var group = a.UnresolvedGroups.Single().Value;
                Check(group.Reasons.SequenceEqual(new[] { "OUTSIDE_QUOTE" }) && group.Events == 5 && group.Examples.Count == 2);
                Check(group.Examples.All(e => e.Candidates.All(c => c.BidSequence < e.Sequence && c.AskSequence < e.Sequence)));
            }
            var combined = new DirectionAmounts();
            var example = row.Primary.UnresolvedGroups.First().Value.Examples[0];
            combined.AddUnresolved(100, ["OUTSIDE_QUOTE", "LOCKED_QUOTE", "OUTSIDE_QUOTE"], () => example);
            combined.AddUnresolved(200, ["LOCKED_QUOTE", "OUTSIDE_QUOTE"], () => example);
            Check(combined.UnresolvedGroups.Count == 1 && combined.UnresolvedGroups.First().Value.Premium == 300);
            Check(combined.UnresolvedGroups.First().Value.Reasons.Length == 2);
            return Task.CompletedTask;
        });
        await test("normal cleanup separated from runtime disconnect", () =>
        {
            var f = new Fixture(); f.Ready(); f.Trade(100, 100, 100, 1);
            var item = RawEventParser.Convert(Guid.Empty, f.Raw(1800000, "connectionClosed"));
            f.Direction.Process(item, null, cleanup: true);
            Check(f.Finish().Diagnostics["CLEANUP_DISCONNECT"] == 1);
            Check(!f.Finish().Totals.Any(x => x.Quality.Contains("OBSERVATION_GAP")));
            return Task.CompletedTask;
        });
    }
    private sealed class Fixture
    {
        public DateTime Start = new(2026, 9, 16, 13, 30, 0, DateTimeKind.Utc);
        public OptionContractDescriptor Contract = new(1, "QQQ", new(2026, 9, 16), 100, OptionRight.Call, "QQQ", "SMART", 100, "20260916:0930-20260916:1600");
        public ProbeAnalyzer Legacy;
        public DirectionAnalyzer Direction;
        public int ClockOffset;
        private long _seq;
        public Fixture(bool outside = false) { Legacy = new(1000, Start, Start.AddMinutes(30)); Direction = new(1000, Start, Start.AddMinutes(30), outside); }
        public IbRawMarketEvent Raw(long ms, string kind, int? field = null, string? value = null, int? type = null)
            => new(++_seq, Start.AddMilliseconds(ms + ClockOffset), ms, kind, 1, Contract, field, value, MarketDataType: type);
        public DirectionEvidence? Apply(IbRawMarketEvent raw) { var item = RawEventParser.Convert(Guid.Empty, raw); return Direction.Process(item, Legacy.Process(item)); }
        public void Send(long ms, string kind, int? field = null, string? value = null, int? type = null) => Apply(Raw(ms, kind, field, value, type));
        public void Ready() { Send(0, "marketDataType", type: 1); Quote(0, 1, 1); Quote(0, 2, 2); }
        public void Quote(long ms, int field, decimal value) => Send(ms, "tickPrice", field, value.ToString(CultureInfo.InvariantCulture));
        public string Payload(long source, decimal cumulative, decimal price) => FormattableString.Invariant($"{price};1;{new DateTimeOffset(Start.AddMilliseconds(source)).ToUnixTimeMilliseconds()};{cumulative};1;true");
        public DirectionEvidence Trade(long source, long receive, decimal cumulative, decimal price) => Apply(Raw(receive, "tickString", 77, Payload(source, cumulative, price)))!;
        public DirectionReport Finish() => Direction.Finish(Legacy.Rows(), new(100, 0, false, false, 0));
    }
}
