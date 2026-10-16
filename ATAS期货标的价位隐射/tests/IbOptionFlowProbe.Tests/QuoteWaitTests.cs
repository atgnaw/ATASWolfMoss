using IbOptionFlowProbe;
namespace IbOptionFlowProbe.Tests;

internal static class QuoteWaitTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("quote wait assertion failed"); }
    private static DirectionMatch Match(DirectionEvidence e, int wait, string method = "Midpoint")
        => e.WaitMatches.Single(x => x.ScenarioId == $"Envelope/{method}/{wait}");
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("conditional size gates, agreement, merge, timeout and small immutable", () =>
        {
            foreach (var ticker in new[] { "QQQ", "SPX" })
            foreach (var large in new[] { false, true })
            foreach (var branch in new[] { "merge", "agree", "timeout", "interrupted", "invalid" })
            {
                var f = new DirectionAnalysisTests.Fixture();
                f.Contract = f.Contract with { Ticker = ticker };
                f.Ready(); f.Trade(100, 100, 100, 1.6m);
                var threshold = ticker == "QQQ" ? 100 : 50;
                var size = large ? threshold : threshold - 1;
                var raw = f.Raw(1000, "tickString", 77, f.Payload(900, 100 + size, 1.6m).Replace(";1;", $";{size};", StringComparison.Ordinal));
                var e = f.Apply(raw)!;
                Check(e.Disposition == "ACCEPTED" && e.IsLargeTrade == large);
                if (!large) Check(e.ConditionalMatches.Count == 3);
                else Check(e.ConditionalMatches.Count == 0);
                if (branch == "merge") f.Quote(1050, 2, 2.2m);
                if (branch == "agree") f.Quote(1049, 2, 2.1m);
                if (branch == "invalid") { f.Quote(1049, 2, -1); f.Send(1051, "tickSize", 0, "10"); }
                if (branch == "timeout") f.Send(1051, "tickSize", 0, "10");
                f.Finish();
                var m = e.ConditionalMatches.Single(x => x.ScenarioId == "Conditional/Midpoint/50");
                Check(m.Branch == (!large ? "SMALL_LATEST" : branch switch {
                    "merge" => "LARGE_MERGED", "agree" => "LARGE_AGREE", "timeout" => "LARGE_TIMEOUT",
                    "invalid" => "LARGE_FIRST_INVALID_FALLBACK", _ => "LARGE_INTERRUPTED" }));
                Check(m.Sign == (!large || branch is "agree" or "timeout" or "invalid" ? 1 : 0));
            }
            Check(LargeTradeSettings.Parse(["--large-spx", "12", "--large-qqq", "34"]) == new LargeTradeSettings(34, 12));
            foreach (var args in new[] { new[] { "--large-qqq", "0" }, new[] { "--bad", "1" }, new[] { "--large-spx" }, new[] { "--large-spx", "1", "--large-spx", "2" } })
            { var threw = false; try { LargeTradeSettings.Parse(args); } catch (ArgumentException) { threw = true; } Check(threw); }
            return Task.CompletedTask;
        });
        await test("conditional first invalid quote immediately falls back and ignores recovery", () =>
        {
            foreach (var delay in new[] { 49, 50, 51 })
            {
                var f = new DirectionAnalysisTests.Fixture();
                f.Ready(); f.Quote(10, 1, 1.06m); f.Quote(10, 2, 1.07m);
                f.Trade(100, 100, 100, 1.07m);
                var e = f.Apply(f.Raw(1000, "tickString", 77, f.Payload(900, 200, 1.07m).Replace(";1;", ";100;")))!;
                f.Quote(1047, 1, 1.08m); // old Ask 1.07, crossed
                Check(e.ConditionalMatches.Count == 3 && e.ConditionalSkippedQuotes == 1);
                Check(e.WaitMatches.Single(x=>x.ScenarioId=="Envelope/Midpoint/50").Reason=="CROSSED_QUOTE");
                f.Quote(1000 + delay, 2, 1.09m);
                f.Send(1500, "tickSize", 0, "10");
                var m = e.ConditionalMatches.Single(x=>x.ScenarioId=="Conditional/Midpoint/50");
                Check(e.Ready && e.ConditionalSkippedReasons["CROSSED_QUOTE"] == 1);
                Check(m.Branch == "LARGE_FIRST_INVALID_FALLBACK");
                Check(m.DecidedUtc == f.Start.AddMilliseconds(1047));
                Check(e.ConditionalWaitDecision!.Status == "FIRST_INVALID_PRE_ONLY");
                Check(e.ConditionalSimulatedQuote == null);
                Check(m.Sign == 1);
                Check(e.ConditionalNextQuote!.Quote!.Ask!.Price == 1.07m);
                Check(e.ConditionalFirstRejectedQuote!.Quote!.Bid!.Price == 1.08m);
            }
            return Task.CompletedTask;
        });
        await test("conditional invalid candidate does not hide disconnection or invalid pre quote", () =>
        {
            foreach(var invalidBefore in new[] { false, true })
            {
                var f=new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.6m);
                if(invalidBefore) f.Quote(900,1,3);
                var e=f.Apply(f.Raw(1000,"tickString",77,f.Payload(900,200,1.6m).Replace(";1;",";100;")))!;
                if(invalidBefore) f.Quote(1040,1,4);
                if(invalidBefore) f.Send(1051,"tickSize",0,"10"); else f.Send(1045,"connectionClosed");
                var m=e.ConditionalMatches.Single(x=>x.ScenarioId=="Conditional/Midpoint/50");
                Check(m.Sign==0 && m.Reason==(invalidBefore?"CROSSED_QUOTE":"WAIT_INTERRUPTED"));
            }
            return Task.CompletedTask;
        });
        await test("wait exact thresholds are inclusive; late quote never replaces timeout", () =>
        {
            foreach (var delay in new[] { 0, 49, 50, 51, 100, 101, 200, 201, 400, 401 })
            {
                var f = new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.6m);
                var e = f.Trade(900,1000,101,1.6m);
                f.Quote(1000 + delay,2,2.2m); f.Send(1500,"tickSize",0,"10");
                Check(e.Ready && e.WaitMatches.Count == 12);
                foreach (var w in DirectionSettings.WaitThresholds)
                {
                    Check(e.WaitDecisions.Single(x => x.WaitMs == w).Status == (delay <= w ? "PAIRED" : "TIMEOUT_PRE_ONLY"));
                    Check(Match(e,w).Reason == (delay <= w ? "MIDPOINT_UNKNOWN" : "INSIDE_ABOVE_MIDPOINT"));
                }
            }
            return Task.CompletedTask;
        });
        await test("wait ignores size and repeated price; uses only first price change", () =>
        {
            var f = new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.6m);
            var e = f.Trade(900,1000,101,1.6m);
            f.Send(1010,"tickSize",0,"100"); f.Quote(1020,2,2);
            Check(e.WaitMatches.Count == 0);
            f.Quote(1047,2,2.2m); var seq=e.NextQuote!.Quote!.Ask!.Sequence;
            f.Quote(1050,2,1.8m);
            Check(e.Ready && e.WaitMatches.All(m => m.Sign == 0));
            Check(e.NextQuote.Quote.Ask.Sequence == seq);
            return Task.CompletedTask;
        });
        await test("wait cannot borrow future tick direction or other contract quote", () =>
        {
            var f = new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.55m);
            var e = f.Trade(900,1000,101,1.55m); // no prior price change; becomes simulated midpoint
            f.Apply(f.Raw(1010,"tickPrice",2,"2.1") with { Contract = f.Contract with { ConId = 2 } });
            Check(!e.Ready);
            f.Trade(1010,1020,102,1.6m); // would establish uptick, but cannot affect e
            f.Quote(1040,2,2.1m);
            Check(Match(e,50,"MidpointTick").Reason == "TICK_NO_CHANGE");
            Check(e.TickSign == 0);
            return Task.CompletedTask;
        });
        await test("wait interruption and truncated observation stay unknown, no timeout fiction", () =>
        {
            foreach (var why in new[] { "end", "disconnect", "type", "request", "clock" })
            {
                var f = new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.6m);
                var e = f.Trade(900,1000,101,1.6m);
                if (why == "disconnect") f.Send(1020,"connectionClosed");
                if (why == "type") f.Send(1020,"marketDataType",type:3);
                if (why == "request") f.Apply(f.Raw(1020,"tickPrice",2,"2.2") with { RequestId = 2 });
                if (why == "clock") { f.ClockOffset=1000; f.Send(1020,"tickSize",0,"10"); }
                f.Finish();
                Check(e.Ready && e.WaitMatches.All(m=>m.Reason=="WAIT_INTERRUPTED"));
            }
            return Task.CompletedTask;
        });
        await test("wait methods U15 and mid conservation preserve independent scope buckets", () =>
        {
            foreach (var outside in new[] { false,true })
            {
                var f = new DirectionAnalysisTests.Fixture(outside); f.Ready(); f.Trade(100,100,100,3);
                var e = f.Trade(900,1000,101,3); f.Quote(1040,2,2.2m);
                Check(Match(e,50).Sign == (outside ? 1 : 0));
                var row=f.Finish().Totals.Single();
                foreach(var w in DirectionSettings.WaitThresholds)
                { var a=row.Scenarios[$"Envelope/Midpoint/{w}"]; Check(a.Metrics.Total == 300 && a.WaitCounts["PAIRED"]==1 && a.WaitPremiums["PAIRED"]==300); }
            }
            var mid=new DirectionAnalysisTests.Fixture(); mid.Ready(); mid.Trade(100,100,100,1.5m);
            var m=mid.Trade(900,1000,101,1.5m); mid.Quote(1040,1,2); // locked quote -> Mid
            Check(Match(m,50).Reason=="MIDPOINT_UNKNOWN");
            return Task.CompletedTask;
        });
        await test("wait expires in silence, but never extends into another trading segment", () =>
        {
            var f=new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,1.6m);
            var e=f.Trade(900,1000,101,1.6m); f.Send(1500,"tickSize",0,"10");
            Check(e.WaitDecisions.All(x=>x.Status=="TIMEOUT_PRE_ONLY") && e.WaitMatches.All(x=>x.Sign==1||x.Reason=="INSIDE_SPREAD"));
            var g=new DirectionAnalysisTests.Fixture(); g.Ready(); g.Trade(23399000,23399000,100,1.6m);
            var b=g.Trade(23399900,23399900,101,1.6m); g.Quote(23400000,2,2.2m);
            Check(b.WaitDecisions.Single(x=>x.WaitMs==50).Status=="TIMEOUT_PRE_ONLY");
            Check(Match(b,100).Reason=="WAIT_INTERRUPTED");
            return Task.CompletedTask;
        });
        await test("envelope keeps minimum bid maximum ask and their original provenance", () =>
        {
            var f=new DirectionAnalysisTests.Fixture(); f.Ready();
            AlignmentEvidence Quote(long seq,decimal bid,decimal ask) => new("test",f.Start,100,true,
                new(100,seq,new(seq,bid,90,f.Start,bid<=0),new(seq+1,ask,95,f.Start,ask<=0),10,20,seq+2,seq+3,1),null,10,5);
            var a=Quote(10,2.81m,2.89m); var b=Quote(20,2.82m,2.91m);
            var merged=f.Direction.SimulateEnvelope(a,b);
            Check(merged.Quote!.Bid!.Price==2.81m && merged.Quote.Ask!.Price==2.91m);
            Check(merged.Quote.Bid.Sequence==10 && merged.Quote.Ask.Sequence==21);
            Check(DirectionClassifier.Classify(merged,2.86m,"Midpoint",-1,0).Reason=="MIDPOINT_UNKNOWN");
            var reverse=f.Direction.SimulateEnvelope(b,a);
            Check(reverse.Quote!.Bid!.Price==2.81m && reverse.Quote.Ask!.Price==2.91m);
            var tie=f.Direction.SimulateEnvelope(a,Quote(30,2.81m,2.89m));
            Check(tie.Quote!.Bid!.Sequence==10 && tie.Quote.Ask!.Sequence==11);
            foreach(var input in new[]{Quote(40,3,2),Quote(50,0,2), a with {Quote=a.Quote! with{DataType=3}},a with {Quote=a.Quote! with{BidSize=0}},a with {Quote=a.Quote! with{Ask=null}}})
            {
                Check(f.Direction.SimulateEnvelope(a,input).Error!=null);
                Check(f.Direction.SimulateEnvelope(input,b).Error!=null);
            }
            return Task.CompletedTask;
        });
        await test("envelope can resolve old outside trade without U15 override or U26", () =>
        {
            var f=new DirectionAnalysisTests.Fixture(); f.Ready(); f.Trade(100,100,100,2.1m);
            var e=f.Trade(900,1000,101,2.1m); f.Quote(1040,2,2.2m);
            Check(e.Matches.Single(x=>x.ScenarioId=="Latest/Midpoint/-1").Reason=="OUTSIDE_QUOTE");
            Check(Match(e,50).Sign==1 && Match(e,50).Reason=="INSIDE_ABOVE_MIDPOINT");
            Check(e.WaitMatches.All(x=>x.Reason is not ("DIRECTION_CONFLICT" or "MID_DIRECTION_DISAGREEMENT" or "UNRESOLVED_CANDIDATE")));
            return Task.CompletedTask;
        });
    }
}
