using System.Collections;
using System.Diagnostics;
using System.Reflection;
using WolfMoss.ATAS.PriceMapping;
using WolfMoss.ATAS.PriceMapping.Core;

internal static class BuyStrengthIntegrationTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        RecoveryStateAndReasons();
        var formatted = OptionStrengthPresentation.Details(OptionBuyStrength.Create(
            new(191.00000000000000000000000000m, 56.0000m, 0.1250m, 0m), new(191,56,0,0)));
        Check(formatted.Contains("张数：买 191 / 卖 56"), "remove decimal scale without rounding contracts");
        Check(formatted.Contains("中性 0.125 / 未知 0"), "retain meaningful fractional quantity");
        var wrapped = StatusUiPresentation.Wrap(formatted, 30, 100, s => s.Length);
        Check(wrapped.All(s => s.Length <= 30), "tooltip lines fit available width");
        Check(StatusUiPresentation.Wrap(formatted, 30, 3, s => s.Length).Length == 3, "tooltip respects available height");
        var (client,socket)=IbCoordinationTests.Connected(); await using var cleanup=client;
        var now=DateTime.UtcNow; var start=now.Date; var end=start.AddDays(1);
        client.ConfigureBuyStrengthCalendar(_=>new([new(start,end)],[new(start,end)]));
        var c=new OptionContractDescriptor(987,"QQQ",DateOnly.FromDateTime(now),700,OptionRight.Call,"QQQ","SMART",100,"");
        var calls=0;
        using var first=await client.SubscribeAsync([c],new(false,true,false,true),_=>calls++,1,default);
        using var second=await client.SubscribeAsync([c],new(false,true,false,true),_=>calls++,1,default);
        Check(client.ActiveMarketDataLines==1 && socket.MaximumLines==1,"233+375 and two charts must share one line");
        var request=socket.Operations.Last(o=>o.Op=="request");
        Check(request.Ticks=="375,233","combined generic ticks");
        var dict=(IDictionary)typeof(ReflectionIbOptionGatewayClient).GetField("_subscriptionsByConId",Private)!.GetValue(client)!;
        var sub=dict[c.ConId]!; var field=sub.GetType().GetField("Strength")!;
        var state=(OptionBuyStrengthState)field.GetValue(sub)!;
        var hook=typeof(ReflectionIbOptionGatewayClient).GetMethod("ObserveBuyStrength",Private)!;
        void Event(string kind,params object[] args)=>hook.Invoke(client,[kind,args]);
        Event("marketDataType",request.Id,1);
        Event("tickPrice",request.Id,1,1m);
        Event("tickPrice",request.Id,2,2m);
        Event("tickSize",request.Id,0,10m);
        Event("tickSize",request.Id,3,10m);
        var stamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var handler=typeof(ReflectionIbOptionGatewayClient).GetMethod("HandleCallback",Private)!;
        var method=typeof(BuyStrengthIntegrationTests).GetMethod(nameof(tickString))!;
        handler.Invoke(client,[method,new object[]{request.Id,48,$"1;10;{stamp};10;1;true"}]);
        handler.Invoke(client,[method,new object[]{request.Id,48,$"2;10;{stamp};20;1;true"}]);
        var source=DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime;
        var bucket=start.AddMinutes((int)(source-start).TotalMinutes/5*5);
        var read=client.ReadBuyStrength(c.ConId,bucket,bucket.AddMinutes(5),bucket.AddMinutes(5).AddSeconds(3));
        Check(read.Midpoint.Buy==10 && read.AtQuote.Buy==10,"once per ingress, not twice per chart");
        Check(calls==4,"both charts receive both cumulative updates");
        var again=client.ReadBuyStrength(c.ConId,bucket,bucket.AddMinutes(5),bucket.AddMinutes(5).AddSeconds(4));
        Check(ReferenceEquals(read,again),"immutable completed snapshot cached");
        Check(state.CachedBuckets<=12,"bounded shared buckets");
        // Farm interruption must not leave an otherwise live subscription permanently unclassified.
        Event("error", -1, 2103, "test farm disconnected");
        Check(state.AwaitingMarketType, "interruption clears confirmation");
        await client.RepairSubscriptionsAsync(now.AddSeconds(10),1,default);
        var repaired=socket.Operations.Last(o=>o.Op=="request");
        Check(repaired.Id!=request.Id && repaired.Ticks=="375,233" && socket.MaximumLines==1,"paced replacement shares line and retains ticks");
        Event("marketDataType",request.Id,1);
        Check(state.AwaitingMarketType,"old generation type ignored");
        Event("marketDataType",repaired.Id,3);
        Check(!state.AwaitingMarketType,"explicit delayed type is confirmed, not treated as realtime");
        var requestCount=socket.Operations.Count(o=>o.Op=="request");
        await client.RepairSubscriptionsAsync(now.AddSeconds(45),1,default);
        Check(socket.Operations.Count(o=>o.Op=="request")==requestCount,"no retries for confirmed delayed feed");
        Event("error", -1, 2103, "test farm disconnected");
        for(var i=0;i<5;i++) await client.RepairSubscriptionsAsync(now.AddSeconds(80+i*40),1,default);
        Check(socket.Operations.Count(o=>o.Op=="request")==requestCount+3,"unconfirmed retry limit");
        Event("error", -1, 2104, "test farm restored");
        await client.RepairSubscriptionsAsync(now.AddSeconds(300),1,default);
        repaired=socket.Operations.Last(o=>o.Op=="request");
        Check(socket.Operations.Count(o=>o.Op=="request")==requestCount+4,"restoration rearms exhausted recovery");
        Event("marketDataType",repaired.Id,1);
        Check(!state.AwaitingMarketType,"repaired subscription confirmed");
        first.Dispose(); second.Dispose();
        Check(client.ActiveMarketDataLines==0,"last consumer releases lines");

        var rows=new[]{new OptionStrikeRow(700,null,null,100,200,10,20)};
        var cache=new OptionRenderCache(); var old=cache.Get(rows,false,OptionDataStatus.Live);
        var strength=OptionBuyStrength.Create(new(60,40,0,0),new(60,40,0,0));
        var newer=cache.Get([rows[0] with{CallBuyStrength=strength,PutBuyStrength=strength}],false,OptionDataStatus.Live);
        Check(newer.Maximum==old.Maximum && newer.Rows[0].CallLabel==old.Rows[0].CallLabel && newer.Rows[0].PutLabel==old.Rows[0].PutLabel,"amounts and bar scale unchanged");
        foreach(var call in new[]{true,false})
        {
            var previous=-1;
            for(var i=0;i<=100;i++)
            {
                var color=OptionStrengthPresentation.Color(OptionBuyStrength.Create(new(i,100-i,0,0),new(i,100-i,0,0)),call);
                var brightness=color.R+color.G+color.B; Check(brightness>=previous,"monotonic fixed brightness"); previous=brightness;
            }
        }
        var partial=OptionBuyStrength.Create(new(60,40,0,0),new(60,40,0,0),true);
        Check(partial.Marker=="?" && OptionBuyStrength.Create(new(60,40,0,0),new(50,50,0,0)).Marker=="×","markers");
        var indicator=Activator.CreateInstance(PerformanceDiagnosticTests.LoadPro().GetType("WolfMoss.ATAS.PriceMapping.FuturesReferencePriceAxisDealerHeatmapIndicator",true)!)!;
        var type=indicator.GetType(); var toggle=type.GetProperty("ShowOptionBuyStrength")!;
        Check(!(bool)toggle.GetValue(indicator)!,"default disabled");
        var scope=type.GetProperty("OptionFlowTradeScope")!.GetValue(indicator);
        toggle.SetValue(indicator,true);
        Check(Equals(scope,type.GetProperty("OptionFlowTradeScope")!.GetValue(indicator)),"375 preference not overwritten");
        toggle.SetValue(indicator,false);
    }
    public static void tickString(int request,int field,string text) { }

    private static void RecoveryStateAndReasons()
    {
        var origin=new DateTime(2026,10,13,13,30,0,DateTimeKind.Utc);
        var c=new OptionContractDescriptor(998,"QQQ",new(2026,10,13),743,OptionRight.Call,"QQQ","SMART",100,"");
        var calendar=new OptionBuyStrengthCalendar([new(origin,origin.AddHours(7))],[new(origin,origin.AddHours(7))]);
        long T(DateTime t)=>(long)(t-origin).TotalMilliseconds+1000;
        static string P(DateTime t,int total,decimal price=2)=>FormattableString.Invariant($"{price};10;{new DateTimeOffset(t).ToUnixTimeMilliseconds()};{total};1;true");
        void Quotes(OptionBuyStrengthState s,DateTime t,int req=1)
        {
            s.Quote(req,1,1,false,false,t,T(t)); s.Quote(req,2,2,false,false,t,T(t));
            s.Quote(req,0,10,false,true,t,T(t)); s.Quote(req,3,10,false,true,t,T(t));
        }
        var reasonCases=new[]
        {
            (DirectionUnknownReason.MarketTypeUnconfirmed,0,(decimal?)1,(decimal?)2,(decimal?)10,false),
            (DirectionUnknownReason.NonRealtime,3,(decimal?)1,(decimal?)2,(decimal?)10,false),
            (DirectionUnknownReason.MissingQuote,1,(decimal?)null,(decimal?)2,(decimal?)10,false),
            (DirectionUnknownReason.InvalidPrice,1,(decimal?)0,(decimal?)2,(decimal?)10,false),
            (DirectionUnknownReason.QuoteFlagged,1,(decimal?)1,(decimal?)2,(decimal?)10,true),
            (DirectionUnknownReason.InvalidSize,1,(decimal?)1,(decimal?)2,(decimal?)0,false),
            (DirectionUnknownReason.CrossedQuote,1,(decimal?)3,(decimal?)2,(decimal?)10,false)
        };
        foreach(var (reason,type,bid,ask,size,flag) in reasonCases)
        {
            var s=new OptionBuyStrengthState(c,calendar,1000); s.Start(1,origin); s.MarketType(1,type,origin,T(origin));
            s.Quote(1,1,bid,flag,false,origin,T(origin)); s.Quote(1,2,ask,false,false,origin,T(origin));
            s.Quote(1,0,size,false,true,origin,T(origin)); s.Quote(1,3,10,false,true,origin,T(origin));
            s.Trade(1,P(origin,10),origin,T(origin));
            s.Trade(1,P(origin.AddSeconds(1),20),origin.AddSeconds(1),T(origin.AddSeconds(1)));
            var value=s.Read(origin,origin.AddMinutes(5),origin.AddMinutes(5).AddSeconds(3));
            Check(value.Midpoint.Unknown==10 && value.MidpointUnknownReasons[reason]==10 && value.AtQuoteUnknownReasons[reason]==10,"reason counted once "+reason);
            Check(Enum.GetValues<DirectionUnknownReason>().Sum(r=>value.MidpointUnknownReasons[r])==10,"reason totals");
            Check(OptionStrengthPresentation.Details(value).Any(l=>l.StartsWith("未知：") && l.Contains("10张")),"visible reason");
        }
        var state=new OptionBuyStrengthState(c,calendar,1000); state.Start(1,origin); state.MarketType(1,1,origin,T(origin)); Quotes(state,origin);
        state.Trade(1,P(origin,10),origin,T(origin));
        var t=origin.AddSeconds(1); state.Trade(1,P(t,20,1.75m),t,T(t));
        var inside=state.Read(origin,origin.AddMinutes(5),origin.AddMinutes(5).AddSeconds(3));
        Check(inside.Midpoint.Buy==10 && inside.Midpoint.Unknown==0 && inside.AtQuoteUnknownReasons.InsideSpread==10,"inside spread only unknown for at quote");
        // A wall-clock adjustment invalidates old quotes, not a confirmed subscription type.
        t=origin.AddMinutes(6); state.Quote(1,1,1,false,false,t,2001);
        Check(!state.AwaitingMarketType,"clock jump preserves confirmed market type");
        Quotes(state,t);
        state.Trade(1,P(t,30),t,T(t)); t=t.AddSeconds(1);
        Check(state.Trade(1,P(t,40),t,T(t))?.Midpoint==OptionDirection.Buy,"fresh quotes recover after clock correction without type callback");
        state.Start(2,t);
        Quotes(state,t,2); state.Trade(2,P(t,50),t,T(t)); t=t.AddSeconds(1);
        Check(state.Trade(2,P(t,60),t,T(t))?.Midpoint==OptionDirection.Unknown && state.NeedsMarketTypeRefresh(t.AddSeconds(5)),"network reset cannot infer realtime from quotes");
        state.MarketType(2,1,t,T(t)); Quotes(state,t,2); state.Trade(2,P(t,70),t,T(t));
        var complete=origin.AddMinutes(10);
        Check(state.Trade(2,P(complete,80),complete,T(complete))?.Midpoint==OptionDirection.Buy,"explicit confirmation and fresh quotes recover");
        Check(state.Read(complete,complete.AddMinutes(5),complete.AddMinutes(5).AddSeconds(3)).Status==BuyStrengthStatus.Ready,"next complete bucket ready");
        // Session boundaries retain type but require fresh bid/ask and sizes.
        var split=origin.AddHours(1);
        var sessionState=new OptionBuyStrengthState(c,new([new(origin,split),new(split,split.AddHours(1))],[new(origin,split.AddHours(2))]),1000);
        sessionState.Start(1,origin); sessionState.MarketType(1,1,origin,T(origin)); Quotes(sessionState,origin);
        Quotes(sessionState,split);
        sessionState.Trade(1,P(split,10),split,T(split)); t=split.AddSeconds(1);
        Check(sessionState.Trade(1,P(t,20),t,T(t))?.Midpoint==OptionDirection.Buy,"new session doesn't wait forever for type callback");
    }
}
