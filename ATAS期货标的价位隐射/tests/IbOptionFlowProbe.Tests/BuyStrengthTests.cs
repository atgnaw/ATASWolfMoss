using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using IbOptionFlowProbe;
using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe.Tests;

internal static class BuyStrengthTests
{
    private static OptionBuyStrengthCalendar Calendar(OptionContractDescriptor c) => new(
        IbTradingHoursParser.Parse(c.TradingHours,c.TradingTimeZoneId,c.Ticker=="SPX").ToArray(),
        IbTradingHoursParser.Parse(c.TradingHours,c.TradingTimeZoneId,false).ToArray());
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Unit()
    {
        var cases = new[] { (0.9m, OptionDirection.Sell, OptionDirection.Sell), (1m, OptionDirection.Sell, OptionDirection.Sell),
            (1.1m, OptionDirection.Sell, OptionDirection.Unknown), (1.5m, OptionDirection.Mid, OptionDirection.Unknown),
            (1.9m, OptionDirection.Buy, OptionDirection.Unknown), (2m, OptionDirection.Buy, OptionDirection.Buy), (2.1m, OptionDirection.Buy, OptionDirection.Buy) };
        foreach (var (price, mid, at) in cases) Check(OptionBuyStrengthState.Classify(price, 1, 2, 1, 1, 1) == (mid, at), "classification " + price);
        Check(OptionBuyStrengthState.Classify(1, 1, 1, 1, 1, 1).Mid == OptionDirection.Mid, "locked");
        Check(OptionBuyStrengthState.Classify(1, 2, 1, 1, 1, 1).Mid == OptionDirection.Unknown, "crossed");
        Check(OptionBuyStrengthState.Classify(1, 1, 2, 0, 1, 1).Mid == OptionDirection.Unknown, "zero size");
        Check(OptionBuyStrengthState.Classify(1, 1, 2, null, 1, 1).Mid == OptionDirection.Unknown, "missing size");
        Check(OptionBuyStrengthState.Classify(1, 1, 2, 1, 1, 3).Mid == OptionDirection.Unknown, "delayed");
        Check(OptionBuyStrengthState.Classify(1, 1, 2, 1, 1, 1, true).Mid == OptionDirection.Unknown, "flagged");
        Check(OptionBuyStrength.Create(new(60,40,0,0),new(59,41,0,0)).Status == BuyStrengthStatus.Ready, "1pp inclusive");
        Check(OptionBuyStrength.Create(new(60,40,0,0),new(58,42,0,0)).Status == BuyStrengthStatus.Filtered, "2pp");
        Check(OptionBuyStrength.Create(new(60,40,0,0),new(0,0,0,100)).Status == BuyStrengthStatus.Unavailable, "no directional");
        Check(OptionBuyStrength.Create(new(60,40,0,0),new(60,40,0,0),true).Status == BuyStrengthStatus.Partial, "partial");
        var c = new OptionContractDescriptor(1,"QQQ",new(2026,10,1),600,OptionRight.Call,"QQQ","SMART",100,"20261001:0930-20261001:1600");
        var state = new OptionBuyStrengthState(c,Calendar(c),1000); var time = new DateTime(2026,10,1,13,29,59,DateTimeKind.Utc);
        state.Start(7,time);
        long tick=1;
        void Quotes(DateTime t) { state.MarketType(7,1,t,tick++); state.Quote(7,1,1,false,false,t,tick++); state.Quote(7,2,2,false,false,t,tick++); state.Quote(7,0,10,false,true,t,tick++); state.Quote(7,3,10,false,true,t,tick++); }
        time = time.AddSeconds(2); Quotes(time);
        string Payload(DateTime t,int total,decimal price=2) => FormattableString.Invariant($"{price};1;{new DateTimeOffset(t).ToUnixTimeMilliseconds()};{total};1;true");
        Check(state.Trade(7,Payload(time,1),time,tick++)?.Disposition == "BASELINE","baseline");
        var text = Payload(time,2,1);
        Check(state.Trade(7,text,time,tick++)?.Disposition == "ACCEPTED","same time distinct print");
        Check(state.Trade(7,text,time,tick++)?.Disposition == "DUPLICATE","duplicate");
        Check(state.Trade(6,text,time,tick++) == null,"old request");
        state.Stop(time); Check(state.Trade(7,text,time,tick++) == null,"disabled");
        // Quotes do not allocate a history node per callback.
        state.Start(7,time); Quotes(time);
        for(var i=0;i<100;i++)state.Quote(7,1,1,false,false,time,tick++);
        var allocated=GC.GetAllocatedBytesForCurrentThread(); var watch=Stopwatch.StartNew();
        for(var i=0;i<100000;i++)state.Quote(7,1,1,false,false,time,tick++);
        watch.Stop(); allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        Check(allocated<4096,"quote allocations "+allocated);
        Console.WriteLine($"BUY STRENGTH quotes: 100000 callbacks {watch.Elapsed.TotalMilliseconds:F2} ms; {allocated} bytes");
        LifecycleAndPerformance(c);
    }

    private static void LifecycleAndPerformance(OptionContractDescriptor c)
    {
        var origin=new DateTime(2026,10,1,13,30,0,DateTimeKind.Utc);
        var state=new OptionBuyStrengthState(c,Calendar(c),1000);
        long T(DateTime t)=>(long)(t-origin).TotalMilliseconds+1000;
        void Quote(int request,DateTime t)
        {
            state.MarketType(request,1,t,T(t));
            state.Quote(request,1,1,false,false,t,T(t)); state.Quote(request,2,2,false,false,t,T(t));
            state.Quote(request,0,1,false,true,t,T(t)); state.Quote(request,3,1,false,true,t,T(t));
        }
        static string Payload(DateTime t,int total,decimal price=2)=>FormattableString.Invariant($"{price};1;{new DateTimeOffset(t).ToUnixTimeMilliseconds()};{total};1;true");
        DirectionPrint? Print(int req,DateTime t,int total,decimal price=2)=>state.Trade(req,Payload(t,total,price),t,T(t));
        state.Start(1,origin); Quote(1,origin);
        Print(1,origin,1);
        var next=origin.AddMinutes(5); Print(1,next,2); Print(1,next.AddSeconds(1),3,1);
        var ready=state.Read(next,next.AddMinutes(5),next.AddMinutes(5).AddSeconds(3));
        Check(ready.Status==BuyStrengthStatus.Ready && ready.Midpoint==new DirectionCounts(1,1,0,0),"full next bucket; unlimited stale quotes");
        var late=next.AddMinutes(5).AddSeconds(4);
        state.Trade(1,Payload(next.AddSeconds(2),4),late,T(late));
        Check(ReferenceEquals(ready,state.Read(next,next.AddMinutes(5),late)),"late callback cannot change sealed bucket");
        state.Stop(late); Check(Print(1,late.AddSeconds(1),5)==null,"stopped feed ignores events");
        state.Start(2,origin.AddMinutes(12)); Quote(2,origin.AddMinutes(12)); Print(2,origin.AddMinutes(12),5);
        Check(Print(1,origin.AddMinutes(13),6)==null,"old subscription cannot poison new baseline");
        var resumed=origin.AddMinutes(15); Print(2,resumed,6); Print(2,resumed.AddSeconds(1),7,1);
        Check(state.Read(resumed,resumed.AddMinutes(5),resumed.AddMinutes(5).AddSeconds(3)).Status==BuyStrengthStatus.Ready,"complete bucket after reconnect");
        var cacheLimit=0;
        for(var i=1;i<=600;i++) { Print(2,resumed.AddSeconds(i*10),7+i,i%2==0?2:1); cacheLimit=Math.Max(cacheLimit,state.CachedBuckets); }
        Check(cacheLimit<=12,"long session cache bound");
        foreach(var minutes in new[]{1,3,5,10})
        {
            var t=resumed.AddSeconds(6000);var b=origin.AddMinutes((int)(t-origin).TotalMinutes/minutes*minutes-minutes);
            var v=state.Read(b,b.AddMinutes(minutes),t.AddSeconds(3));
            Check(v.Midpoint.Total>0 && v.Status==BuyStrengthStatus.Ready,"supported interval "+minutes);
        }
        // Input strings are prepared outside the measured hot path, as they are supplied by IB.
        var bench=origin.AddMinutes(120); var inputs=Enumerable.Range(0,10000).Select(i=>Payload(bench.AddMilliseconds(i),i+1,i%2==0?2:1)).ToArray();
        state.Stop(bench);var sw=Stopwatch.StartNew();var bytes=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<inputs.Length;i++)state.Trade(3,inputs[i],bench.AddMilliseconds(i),T(bench)+i);
        sw.Stop();var disabledMs=sw.Elapsed.TotalMilliseconds;var disabledBytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
        state.Start(3,bench);Quote(3,bench);
        for(var i=0;i<100;i++)state.Trade(3,inputs[i],bench.AddMilliseconds(i),T(bench)+i);
        var chunks=new double[99];bytes=GC.GetAllocatedBytesForCurrentThread();sw.Restart();
        for(var chunk=0;chunk<99;chunk++)
        {
            var began=Stopwatch.GetTimestamp();
            for(var i=100+chunk*100;i<200+chunk*100;i++)state.Trade(3,inputs[i],bench.AddMilliseconds(i),T(bench)+i);
            chunks[chunk]=Stopwatch.GetElapsedTime(began).TotalMicroseconds/100;
        }
        sw.Stop();var enabledBytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
        Check(disabledBytes<4096 && enabledBytes<4096,"steady state trade allocations");
        Array.Sort(chunks);
        var snapshotStart=Stopwatch.GetTimestamp();
        var b5=bench;var readAt=b5.AddMinutes(5).AddSeconds(3);
        Check(state.Read(b5,b5.AddMinutes(5),readAt).Midpoint.Total>0,"benchmark reads populated completed snapshot");
        bytes=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<10000;i++)state.Read(b5,b5.AddMinutes(5),readAt);
        Check(GC.GetAllocatedBytesForCurrentThread()-bytes<4096,"cached reads allocations");
        Console.WriteLine($"BUY STRENGTH benchmark: disabled 10000={disabledMs:F2}ms/{disabledBytes}B; enabled 9900={sw.Elapsed.TotalMilliseconds:F2}ms/{enabledBytes}B; batch-mean P95={chunks[94]:F2}us/trade; 10000 reads={Stopwatch.GetElapsedTime(snapshotStart).TotalMilliseconds:F2}ms; peak buckets={cacheLimit}");
    }

    public static void Replay(string directory)
    {
        var manifest=JsonSerializer.Deserialize<ProbeManifest>(File.ReadAllText(Path.Combine(directory,"manifest.json")),ProbeJson.Options)!;
        var summary=JsonSerializer.Deserialize<CaptureSummary>(File.ReadAllText(Path.Combine(directory,"capture-summary.json")),ProbeJson.Options)!;
        var start=summary.CaptureStartedUtc??manifest.CreatedUtc;
        var oracle=new ProbeAnalyzer(manifest.MonotonicFrequency,start,summary.EndedUtc);
        var direction=new DirectionAnalyzer(manifest.MonotonicFrequency,start,summary.EndedUtc,true);
        var states=new Dictionary<long,(int Request,OptionBuyStrengthState State)>();
        var expected=new Dictionary<(long Id,DateTime Start),(DirectionCounts Mid,DirectionCounts At)>();
        var completed=new Dictionary<(long Id,DateTime Start),OptionBuyStrength>();
        long accepted=0,events=0; var watch=Stopwatch.StartNew();
        void Flush(DateTime now)
        {
            foreach(var key in expected.Keys.Where(k=>k.Start.AddMinutes(5).AddSeconds(3)<=now).ToArray())
            {
                var v=states[key.Id].State.Read(key.Start,key.Start.AddMinutes(5),now);
                var e=expected[key];
                Check(v.Midpoint==e.Mid && v.AtQuote==e.At,$"bucket {key} counts differ: {v.Midpoint} vs {e.Mid}");
                var gate=OptionBuyStrength.Create(e.Mid,e.At,v.Status==BuyStrengthStatus.Partial);
                Check(v.Status==gate.Status && v.Gap==gate.Gap,$"bucket {key} gate");
                completed[key]=v; expected.Remove(key);
            }
        }
        var nextFlush=start;
        foreach(var file in Directory.GetFiles(directory,"events-*.jsonl").Order())
        foreach(var line in File.ReadLines(file))
        {
            var item=JsonSerializer.Deserialize<ProbeEvent>(line,ProbeJson.Options)!; var raw=item.Raw; events++;
            if(raw.ReceivedUtc>=nextFlush){Flush(raw.ReceivedUtc);nextFlush=raw.ReceivedUtc.AddSeconds(1);}
            var legacy=oracle.Process(item); var evidence=direction.Process(item,legacy);
            if(raw.Kind=="connectionClosed" || raw.ErrorCode is 1100 or 1101 or 1300 or 2103)
                foreach(var value in states.Values)value.State.Start(value.Request,raw.ReceivedUtc);
            if(raw.Contract is not {} c)continue;
            if(!states.TryGetValue(c.ConId,out var entry)||entry.Request!=raw.RequestId)
            { entry=(raw.RequestId,new(c,Calendar(c),manifest.MonotonicFrequency)); entry.State.Start(raw.RequestId,raw.ReceivedUtc); states[c.ConId]=entry; }
            var state=entry.State;
            if(raw.Kind=="marketDataType")state.MarketType(raw.RequestId,raw.MarketDataType??0,raw.ReceivedUtc,raw.MonotonicTicks);
            else if(raw.Kind is "tickPrice" or "tickSize")
            {
                var valid=decimal.TryParse(raw.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var value);
                state.Quote(raw.RequestId,raw.Field??-1,valid?value:null,raw.PreOpen==true||raw.PastLimit==true,raw.Kind=="tickSize",raw.ReceivedUtc,raw.MonotonicTicks);
            }
            else if(raw.Kind=="tickString" && raw.Field==48)
            {
                var print=state.Trade(raw.RequestId,raw.Value??"",raw.ReceivedUtc,raw.MonotonicTicks)!.Value;
                Check(print.Disposition==legacy?.Disposition,$"sequence {raw.Sequence} disposition {print.Disposition} vs {legacy?.Disposition}");
                if(print.Disposition=="ACCEPTED")
                {
                    accepted++;
                    var segment=Calendar(c).Segments.First(s=>s.Contains(print.SourceUtc));
                    var bucket=segment.StartUtc.AddTicks((print.SourceUtc-segment.StartUtc).Ticks/TimeSpan.FromMinutes(5).Ticks*TimeSpan.FromMinutes(5).Ticks);
                    // Record complete observation buckets; the raw print classifications above cover edge buckets too.
                    if(bucket>=start && bucket.AddMinutes(5)<=summary.EndedUtc && raw.ReceivedUtc<bucket.AddMinutes(5).AddSeconds(3))
                    {
                        var key=(c.ConId,bucket);var count=expected.GetValueOrDefault(key);
                        expected[key]=(count.Mid.Add(print.Midpoint,print.Size),count.At.Add(print.AtQuote,print.Size));
                    }
                    foreach(var method in new[]{"Midpoint","AtQuote"})
                    {
                        var match=evidence!.Matches.Single(m=>m.ScenarioId==$"Latest/{method}/-1");
                        var expectedDirection=match.Sign>0?OptionDirection.Buy:match.Sign<0?OptionDirection.Sell:DirectionClassifier.IsMid(match.Reason)?OptionDirection.Mid:OptionDirection.Unknown;
                        var actual=method=="Midpoint"?print.Midpoint:print.AtQuote;
                        Check(actual==expectedDirection,$"sequence {raw.Sequence} {method}: {actual} vs {expectedDirection} ({match.Reason})");
                    }
                }
            }
            Check(state.CachedBuckets<=12,"bounded buckets");
            if(events%200000==0)Console.WriteLine($"REPLAY {events} events; {accepted} accepted 233 prints; {watch.Elapsed.TotalSeconds:F1}s");
        }
        Flush(summary.EndedUtc);
        Check(accepted>100,"real replay data");
        Console.WriteLine($"PASS BUY STRENGTH REPLAY {events} events, {accepted} accepted 233 prints, {completed.Count} completed buckets; classifications/counts/gates match; {watch.Elapsed.TotalSeconds:F1}s");
    }
}
