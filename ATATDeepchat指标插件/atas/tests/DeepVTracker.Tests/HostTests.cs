using System.Collections.ObjectModel;
using System.Drawing;
using System.Reflection;
using ATAS.Indicators;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.DeepVTracker;
using static CoreTests;

internal static class HostTests
{
    internal sealed class TestCandle(int index, IEnumerable<TradeTick> ticks)
    {
        public int Index = index;
        public List<TradeTick> Ticks = new(ticks);
        public ChartBar Snapshot => Chart(Index, Ticks.ToArray());
    }
    private sealed class Fixture : IDisposable
    {
        public readonly List<TestCandle> Data = [];
        public readonly List<IndicatorCandle> Candles = [];
        public readonly List<CumulativeTradesRequest> Requests = [];
        public readonly DeepVTrackerIndicator Indicator = new();
        public string ChartType = "Range", TimeFrame = "4";
        public int First, Last = int.MaxValue;
        public readonly RecordingRenderContext Context = new();
        private Action<CumulativeTradesRequest, IEnumerable<CumulativeTrade>>? _historyResponse;
        private Action<IEnumerable<MarketDataArg>>? _newTrades;
        private static MethodInfo Method(string name) => typeof(DeepVTrackerIndicator).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public Fixture()
        {
            for (var i = 0; i < 8; i++)
            {
                var time = i * 2L * TimeSpan.TicksPerSecond;
                var last = i == 6 ? 103 : 100;
                Add(new(i, [Tick(time, 100, 10), Tick(time + 4_000_000, 101, 5, TradeSide.Sell),
                    Tick(time + 11_000_000, 99, 10), Tick(time + 16_000_000, last, 5, TradeSide.Sell)]));
            }
            var creator = Proxy.Make<ICandleCreator>((m, a) => m.Name switch
            {
                "get_Candles" => Candles, "get_Name" => "Tracker host test", _ => Proxy.Default(m.ReturnType)
            });
            var source = new CandlePartSeries(creator, DataSeriesType.Close);
            var container = Proxy.Make<IChartContainer>((m, a) => m.Name switch
            {
                "get_Region" => new Rectangle(10, 10, 700, 500), "get_High" => 40m, "get_Low" => 10m,
                "get_Step" => .25m, "get_PriceRowHeight" => 1m, "get_BarsWidth" => 40m, "get_BarSpacing" => 10m,
                "get_FirstVisibleBarNumber" => First, "get_LastVisibleBarNumber" => Math.Min(Last, Candles.Count - 1),
                "get_TotalBars" => Candles.Count,
                "GetXByBar" => 100 + (int)a![0]! * 50,
                "GetYByPrice" => 400 - (int)(((decimal)a![0]! - 20m) * 4),
                _ => Proxy.Default(m.ReturnType)
            });
            var font = new RenderFont("Arial", 10);
            var chart = Proxy.Make<IChart>((m, a) => m.Name switch
            {
                "get_PriceChartContainer" => container, "get_PriceAxisFont" => font,
                "get_ChartType" => ChartType, "get_TimeFrame" => TimeFrame, _ => Proxy.Default(m.ReturnType)
            });
            var info = Proxy.Make<IInstrumentInfo>((m, a) => m.Name switch
            {
                "get_TickSize" => .25m, "get_Instrument" => "NQ TEST", _ => Proxy.Default(m.ReturnType)
            });
            var online = Proxy.Make<IOnlineDataProvider>((m, a) =>
            {
                if (m.Name == "add_HistoricalCumulativeTrades") { _historyResponse += (Action<CumulativeTradesRequest, IEnumerable<CumulativeTrade>>)a![0]!; return null; }
                if (m.Name == "remove_HistoricalCumulativeTrades") { _historyResponse -= (Action<CumulativeTradesRequest, IEnumerable<CumulativeTrade>>)a![0]!; return null; }
                if (m.Name == "add_NewTrades") { _newTrades += (Action<IEnumerable<MarketDataArg>>)a![0]!; return null; }
                if (m.Name == "remove_NewTrades") { _newTrades -= (Action<IEnumerable<MarketDataArg>>)a![0]!; return null; }
                if (m.Name == "RequestCumulativeTrades") { Requests.Add((CumulativeTradesRequest)a![0]!); return null; }
                return m.Name == "get_InstrumentInfo" ? info : Proxy.Default(m.ReturnType);
            });
            var provider = Proxy.Make<IIndicatorDataProvider>((m, a) => m.Name switch
            {
                "get_ChartInfo" => chart, "get_OnlineDataProvider" => online, "get_InstrumentInfo" => info,
                "get_CandlesDataSeries" => new ObservableCollection<CandlePartSeries> { source },
                "get_Panels" => new ObservableCollection<string> { "Chart" }, _ => Proxy.Default(m.ReturnType)
            });
            Indicator.DataProvider = provider; Indicator.SourceDataSeries = source;
            if (Requests.Count == 0) Reload();
        }
        public void Add(TestCandle data)
        {
            Data.Add(data);
            var prices = Proxy.Make<ISupportedPriceInfo>((m, a) => m.Name == "GetAllPriceLevels"
                ? data.Snapshot.Flow.Profile.Select(p => new PriceVolumeInfo { Price = p.Price * .25m, Ask = p.Ask, Bid = p.Bid, Volume = p.Total,
                    Between = p.Total - p.Ask - p.Bid }).ToArray() : Proxy.Default(m.ReturnType));
            var parent = Proxy.Make<IIntCandle>((m, a) =>
            {
                var c = data.Snapshot; var f = c.Flow;
                return m.Name switch
                {
                    "get_Open" => (int)f.Open, "get_High" => (int)f.High, "get_Low" => (int)f.Low, "get_Close" => (int)f.Close,
                    "get_Ask" => (decimal)f.Ask, "get_Bid" => (decimal)f.Bid, "get_Betweens" => (decimal)(f.Volume - f.Ask - f.Bid),
                    "get_Volume" => (decimal)f.Volume, "get_Delta" => (decimal)(f.Ask - f.Bid), "get_Ticks" => (int)c.TickCount,
                    "get_BeginTime" => new DateTime(f.Time), "get_LastTime" => new DateTime(c.LastTime), _ => Proxy.Default(m.ReturnType)
                };
            });
            Candles.Add(new IndicatorCandle(prices, parent, .25m));
        }
        public void Reload() => typeof(BaseIndicator).GetMethod("RecalculateValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(Indicator, null);
        public void Calculate(int? bar = null) => Method("OnCalculate").Invoke(Indicator, [bar ?? (Candles.Count - 1), 0m]);
        public static MarketDataArg Market(TradeTick t) => new() { Time = new DateTime(t.Time), Price = t.Price * .25m, Volume = t.Volume,
            Direction = t.Side == TradeSide.Buy ? TradeDirection.Buy : t.Side == TradeSide.Sell ? TradeDirection.Sell : TradeDirection.Between };
        public void Trade(TradeTick tick) => _newTrades!([Market(tick)]);
        public void Respond(CumulativeTradesRequest? request = null, bool empty = false)
        {
            var groups = empty ? [] : Data.Select(d => new CumulativeTrade { Ticks = d.Ticks.Select(Market).ToList() }).ToArray();
            _historyResponse!(request ?? Requests.Last(), groups);
        }
        public void Draw()
        {
            Context.Fills.Clear(); Context.Outlines.Clear(); Context.Texts.Clear(); Context.Lines.Clear(); Context.SolidTriangles.Clear(); Context.HollowTriangles.Clear();
            Method("OnRender").Invoke(Indicator, [Context, DrawingLayouts.Final]);
        }
        public TradeReplay Replay => (TradeReplay)typeof(DeepVTrackerIndicator).GetField("_replay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Indicator)!;
        public void Dispose() => Indicator.Dispose();
    }
    internal static void Run()
    {
        using var f = new Fixture();
        Check(f.Requests.Count > 0 && f.Requests.Last().Mode == CumulativeTradesMode.Filter, "ATAS adapter requests uncapped raw-bearing cumulative history");
        Check(f.Requests.Last().EndTime.Ticks < f.Data[7].Snapshot.Flow.Time, "History cutoff excludes the incomplete attachment candle");
        Check(f.Indicator.SignalSnapshot.Length == 0 && f.Indicator.Status.Contains("加载"), "Loading does not create synthetic history signals: " + f.Indicator.Status);
        f.Respond();
        Check(f.Indicator.NextIndex == 7 && f.Indicator.SignalSnapshot.Any(s => s.Kind == SignalKind.Control), "Real ATAS candle/tick response generates historical control lines");
        Check(f.Indicator.SignalSnapshot.Any(s => s.Kind == SignalKind.Acceleration), "Historical individual ticks produce acceleration zones");
        var count = f.Indicator.SignalSnapshot.Length;
        for (var i = 0; i < 1000; i++) f.Calculate();
        Check(f.Indicator.SignalSnapshot.Length == count && f.Indicator.NextIndex == 7, "Repeated OnCalculate callbacks do not replay raw prints");
        f.Draw();
        Check(f.Context.Fills.Count > 0 && f.Context.Texts.Contains("PC") && f.Context.Texts.Contains("PE"), "Renderer paints acceleration plus PC/PE labels from actual host replay");
        Check(f.Context.ClipBounds == new Rectangle(0, 0, 800, 600), "Tracker renderer restores chart clipping");
        var requests = f.Requests.Count; f.Indicator.AccelerationAlpha = 80; f.Indicator.AskColor = System.Windows.Media.Colors.Blue; f.Draw();
        Check(f.Requests.Count == requests && f.Context.Fills.All(p => p.Color.A == 80), "Restyle changes fill without re-requesting history");
        f.Context.ThrowOnDraw = true;
        try { f.Draw(); throw new Exception("Expected draw exception"); } catch (TargetInvocationException) { }
        f.Context.ThrowOnDraw = false;
        Check(f.Context.ClipBounds == new Rectangle(0, 0, 800, 600), "Tracker clipping restored even when a draw fails");

        var first = Tick(16 * TimeSpan.TicksPerSecond, 100, 10);
        f.Add(new(8, [first])); f.Calculate(); f.Trade(first);
        Check(f.Indicator.NextIndex == 8 && f.Replay.Volume == 10 && f.Replay.Issues.ContainsKey(7), "Live stream starts at next full bar and marks attachment bar as a gap");
        var second = Tick(17 * TimeSpan.TicksPerSecond, 101, 5, TradeSide.Sell);
        f.Trade(second); // trade arrives before its candle update
        Check(f.Replay.Volume == 10, "Trade arriving before chart update is queued");
        f.Data[8].Ticks.Add(second); f.Calculate();
        Check(f.Replay.Volume == 15, "Queued trade is consumed exactly once when chart catches up");
        var third = Tick(17 * TimeSpan.TicksPerSecond + 2, 100, 10);
        f.Data[8].Ticks.Add(third); f.Calculate(); f.Trade(third);
        Check(f.Replay.Volume == 25, "Chart arriving before trade also pairs correctly");
        for (var i = 0; i < 100; i++) f.Calculate();
        Check(f.Replay.Volume == 25, "Repeated current candle updates do not duplicate volume");
        var next = Tick(18 * TimeSpan.TicksPerSecond, 100, 10); f.Add(new(9, [next])); f.Trade(next); f.Calculate();
        Check(f.Indicator.NextIndex == 9 && !f.Replay.Issues.ContainsKey(8) && f.Replay.Volume == 10,
            "Opening a new bar reconciles and commits prior raw stream");
        var pendingRequests = f.Requests.Count; f.Calculate(8);
        Check(f.Requests.Count == pendingRequests && f.Indicator.NextIndex == 9, "Late previous-bar callback does not reset live state");

        // Independent renderer fixtures cover solid/hollow icons and dashed absorption labels.
        f.Replay.Signals.Clear();
        foreach (var kind in new[] { SignalKind.ExhaustionVolume, SignalKind.ExhaustionCount, SignalKind.Slowdown, SignalKind.Control, SignalKind.Extreme })
            f.Replay.Signals[Signal.Key(kind, 100)] = new Signal { Id = Signal.Key(kind, 100), Kind = kind, Start = 2, End = 5, Created = 2,
                Lower = 100 + (int)kind, Upper = 100 + (int)kind, Up = true, Ask = false, Absorption = true };
        f.Draw();
        Check(f.Context.SolidTriangles.Count == 1 && f.Context.HollowTriangles.Count == 2, "Volume exhaustion is solid; count and slowdown are hollow triangles");
        Check(f.Context.Texts.Contains("AC") && f.Context.Texts.Contains("AE") && f.Context.Lines.Count > 2, "Absorption levels render dashed AC/AE strokes");
        f.First = 7; f.Draw();
        Check(f.Context.Lines.Count == 0 && f.Context.HollowTriangles.Count == 0 && f.Context.SolidTriangles.Count == 0, "Offscreen historical shapes are not drawn");
        f.First = 0;

        var stale = f.Requests.Last(); f.Indicator.ProjectionBars = 12;
        Check(f.Requests.Last().RequestId != stale.RequestId && f.Indicator.SignalSnapshot.Length == 0, "Calculation parameter changes clear output and request a new history generation");
        f.Respond(stale);
        Check(f.Indicator.SignalSnapshot.Length == 0 && f.Indicator.Status.Contains("加载"), "Late response from an older generation is ignored");
        f.Respond();
        Check(f.Indicator.SignalSnapshot.Length > 0, "Matching generation repopulates historical signals");
        f.ChartType = "Range X"; f.Calculate();
        Check(f.Indicator.Status.Contains("加载"), "Changing chart type triggers a fresh detector and history request");
        f.Respond(empty: true);
        Check(f.Indicator.SignalSnapshot.Length == 0 && f.Replay.Issues.Count > 0, "Empty historical tick response produces explicit gaps, not invented trades");
        f.ChartType = "Range"; f.TimeFrame = "unknown"; f.Calculate();
        Check(f.Indicator.Status.Contains("无法识别 Range"), "Unparseable automatic Range size requires an explicit setting");
        f.Indicator.RangeRules = RangeRuleMode.Range; f.Indicator.RangeTicks = 4; f.Respond();
        Check(!f.Indicator.Status.Contains("暂停"), "Explicit Range setting recovers from unsupported chart metadata");

        using var delayed = new Fixture();
        // All of attachment candle's live prints overlap neither historical response nor the accepted live range.
        foreach (var tick in delayed.Data[7].Ticks) delayed.Trade(tick);
        var t8 = new[] { Tick(16 * TimeSpan.TicksPerSecond, 100, 10), Tick(17 * TimeSpan.TicksPerSecond, 101, 10) };
        delayed.Add(new(8, t8)); delayed.Calculate(); foreach (var t in t8) delayed.Trade(t);
        var t9 = Tick(18 * TimeSpan.TicksPerSecond); delayed.Add(new(9, [t9])); delayed.Calculate(); delayed.Trade(t9);
        delayed.Respond();
        Check(delayed.Indicator.NextIndex == 9 && !delayed.Replay.Issues.ContainsKey(8) && delayed.Replay.Volume == 10,
            "Trades buffered during history loading bridge without duplication or dropped full bars");
        var activeBefore = delayed.Indicator.SignalSnapshot.Length;
        delayed.Trade(Tick(17 * TimeSpan.TicksPerSecond));
        Check(delayed.Indicator.Status.Contains("倒序") && delayed.Indicator.SignalSnapshot.All(s => !s.Active),
            "Out-of-order live input rolls back preview and freezes confirmed active drawings");

        using var loading = new Fixture();
        var oldRequest = loading.Requests.Last();
        loading.Indicator.ProjectionBars = 15;
        loading.Indicator.ProjectionBars = 20;
        Check(loading.Requests.Count == 1 && loading.Indicator.Status.Contains("加载"),
            "Rapid recalculation waits for the native outstanding history request");
        loading.Respond(oldRequest);
        Check(loading.Requests.Count == 2 && loading.Indicator.SignalSnapshot.Length == 0 && loading.Indicator.Status.Contains("加载"),
            "Old native response launches only the latest pending generation");
        loading.Respond();
        Check(loading.Indicator.SignalSnapshot.Any(s => s.Kind == SignalKind.Control && s.Lifetime == 20),
            "Latest generation uses the final settings after loading-time edits");
    }
}

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Make<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    { var p = Create<T, Proxy>(); ((Proxy)(object)p).Handler = handler; return p; }
    public static object? Default(Type t) => t == typeof(void) ? null : t.IsValueType ? Activator.CreateInstance(t) : null;
}
