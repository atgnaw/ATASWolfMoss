using System.Collections.ObjectModel;
using System.Drawing;
using System.Reflection;
using ATAS.Indicators;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.DeepMEffort;
using static CoreTests;

internal static class HostTests
{
    internal sealed class Candle
    {
        internal decimal Open = 100, Close = 110, High = 112, Low = 98, Ask = 600, Bid = 400, Betweens;
        internal DateTime Time, LastTime;
    }

    internal static void Run()
    {
        var start = new DateTime(2026, 10, 19, 10, 0, 0);
        var inputs = new List<Candle>
        {
            new() { Time = start, LastTime = start.AddSeconds(20) },
            new() { Open = 110, Close = 120, High = 122, Low = 108, Time = start.AddSeconds(30), LastTime = start.AddSeconds(50) },
            new() { Open = 119, Close = 119, High = 120, Low = 118, Ask = 500, Bid = 500, Time = start.AddSeconds(60), LastTime = start.AddSeconds(80) },
            new() { Open = 119, Close = 119, High = 120, Low = 118, Ask = 500, Bid = 500, Time = start.AddSeconds(90), LastTime = start.AddSeconds(110) },
        };
        var candles = new List<IndicatorCandle>();
        var priceInfo = Proxy.Make<ISupportedPriceInfo>((m, a) => Proxy.Default(m.ReturnType));
        IndicatorCandle Wrap(Candle input)
        {
            var parent = Proxy.Make<IIntCandle>((m, a) => m.Name switch
            {
                "get_Open" => (int)(input.Open * 4), "get_High" => (int)(input.High * 4),
                "get_Low" => (int)(input.Low * 4), "get_Close" => (int)(input.Close * 4),
                "get_Ask" => input.Ask, "get_Bid" => input.Bid, "get_Betweens" => input.Betweens,
                "get_Volume" => input.Ask + input.Bid + input.Betweens, "get_Delta" => input.Ask - input.Bid,
                "get_BeginTime" => input.Time, "get_LastTime" => input.LastTime,
                _ => Proxy.Default(m.ReturnType)
            });
            return new IndicatorCandle(priceInfo, parent, .25m);
        }
        foreach (var input in inputs) candles.Add(Wrap(input));
        var creator = Proxy.Make<ICandleCreator>((m, a) => m.Name switch
        {
            "get_Candles" => candles, "get_Name" => "Effort host test", "get_TickSize" => .25m,
            _ => Proxy.Default(m.ReturnType)
        });
        var source = new CandlePartSeries(creator, DataSeriesType.Close);
        var first = 0;
        var last = 1000;
        var container = Proxy.Make<IChartContainer>((m, a) => m.Name switch
        {
            "get_Region" => new Rectangle(10, 10, 700, 500), "get_High" => 150m, "get_Low" => 80m,
            "get_Step" => .25m, "get_PriceRowHeight" => 1m, "get_BarsWidth" => 40m, "get_BarSpacing" => 10m,
            "get_FirstVisibleBarNumber" => first, "get_LastVisibleBarNumber" => Math.Min(last, candles.Count - 1),
            "get_TotalBars" => candles.Count,
            "GetXByBar" => 100 + (int)a![0]! * 50,
            "GetYByPrice" => 400 - (int)(((decimal)a![0]! - 90m) * 4),
            _ => Proxy.Default(m.ReturnType)
        });
        var font = new RenderFont("Arial", 10);
        var chart = Proxy.Make<IChart>((m, a) => m.Name switch
        {
            "get_PriceChartContainer" => container, "get_PriceAxisFont" => font,
            _ => Proxy.Default(m.ReturnType)
        });
        var info = Proxy.Make<IInstrumentInfo>((m, a) => m.Name switch
        {
            "get_TickSize" => .25m, "get_Instrument" => "NQ TEST", _ => Proxy.Default(m.ReturnType)
        });
        var online = Proxy.Make<IOnlineDataProvider>((m, a) => m.Name switch
        {
            "get_InstrumentInfo" => info, _ => Proxy.Default(m.ReturnType)
        });
        var provider = Proxy.Make<IIndicatorDataProvider>((m, a) => m.Name switch
        {
            "get_ChartInfo" => chart, "get_OnlineDataProvider" => online, "get_InstrumentInfo" => info,
            "get_CandlesDataSeries" => new ObservableCollection<CandlePartSeries> { source },
            "get_Panels" => new ObservableCollection<string> { "Chart" },
            _ => Proxy.Default(m.ReturnType)
        });
        using var indicator = new DeepMEffortIndicator();
        indicator.DataProvider = provider;
        indicator.SourceDataSeries = source;
        var calculate = typeof(DeepMEffortIndicator).GetMethod("OnCalculate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reset = typeof(DeepMEffortIndicator).GetMethod("OnRecalculate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var render = typeof(DeepMEffortIndicator).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reload = typeof(BaseIndicator).GetMethod("RecalculateValues", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        void Calculate(int bar) => calculate.Invoke(indicator, [bar, 0m]);
        void Reset() => reset.Invoke(indicator, null);
        void Reload() => reload.Invoke(indicator, null);
        var context = new RecordingRenderContext();
        void Draw() { context.Fills.Clear(); context.Outlines.Clear(); context.Texts.Clear(); context.Lines.Clear(); render.Invoke(indicator, [context, DrawingLayouts.Final]); }
        Reset(); Calculate(0);
        Check(indicator.ClosedBarCount == 0, "Host history callback 0 does not look ahead into preloaded candles");
        Calculate(1); Calculate(2);
        Check(indicator.ClosedBarCount == 2 && indicator.ZoneSnapshot.Length == 0, "Host pending segment is not displayed early");
        Calculate(3);
        var zone = indicator.ZoneSnapshot.Single();
        Check(zone.Lower == 98 && zone.Upper == 110 && zone.CreatedIndex == 2 && zone.IsActive,
            "Real ATAS IndicatorCandle/Ask/Bid adapter produces expected zone");
        for (var i = 0; i < 1000; i++) Calculate(3);
        Check(indicator.ClosedBarCount == 3 && indicator.ZoneSnapshot.Length == 1,
            "1000 same-bar ticks cannot duplicate signals or advance closed-bar state");
        Draw();
        Check(context.Fills.Count == 1 && context.Fills[0].Rect == new Rectangle(200, 320, 50, 48)
            && context.Fills[0].Color.A == 125 && context.Fills[0].Color.G == 255,
            "Custom drawing maps the zone to expected pixels and default fill");
        Check(context.ClipBounds == new Rectangle(0, 0, 800, 600), "Renderer restores host clip");
        indicator.FillAlpha = 40; indicator.BullishColor = System.Windows.Media.Colors.Blue;
        Draw();
        Check(context.Fills[0].Color.A == 40 && context.Fills[0].Color.B == 255 && indicator.ClosedBarCount == 3,
            "Appearance updates preserve calculation state");
        last = 1; Draw(); Check(context.Fills.Count == 0, "Zones created beyond viewport are not drawn"); last = 1000;
        first = 3; Draw(); Check(context.Fills.Count == 1, "Active zone starting left of viewport is retained"); first = 0;
        context.ThrowOnDraw = true;
        try { Draw(); throw new Exception("Expected render failure"); } catch (TargetInvocationException) { }
        context.ThrowOnDraw = false;
        Check(context.ClipBounds == new Rectangle(0, 0, 800, 600), "Renderer restores clipping after a drawing exception");

        inputs[3].Open = inputs[3].Close = 97; inputs[3].High = 98; inputs[3].Low = 96;
        Calculate(3);
        Check(indicator.ZoneSnapshot[0].IsActive, "Intrabar body breach waits for bar completion");
        var added = new Candle { Open = 97, Close = 97, High = 98, Low = 96, Ask = 500, Bid = 500,
            Time = start.AddSeconds(120), LastTime = start.AddSeconds(140) };
        inputs.Add(added); candles.Add(Wrap(added)); Calculate(4);
        Check(!indicator.ZoneSnapshot[0].IsActive && indicator.ZoneSnapshot[0].EndIndex == 4,
            "Opening next bar finalizes body breach with the correct endpoint");
        var live = indicator.ZoneSnapshot;
        Reset(); for (var i = 0; i < candles.Count; i++) Calculate(i);
        Check(indicator.ZoneSnapshot.SequenceEqual(live), "Historical rebuild reproduces live-confirmed zones");
        Reload();
        Check(indicator.ZoneSnapshot.SequenceEqual(live), "Native RecalculateValues rebuilds without duplicate zones");
        indicator.MinDeltaPercent = 80;
        Draw();
        Check(indicator.ZoneSnapshot.Length == 0 && context.Texts.Any(t => t.Contains("最小 Delta")), "Invalid min/max combination clears stale output and shows a prompt");
        indicator.MinDeltaPercent = 10;
        Check(indicator.ZoneSnapshot.SequenceEqual(live), "Fixing parameters restores deterministic historical output");
        inputs[1].Betweens = 5; Reload(); Draw();
        Check(indicator.ZoneSnapshot.Length == 0 && context.Texts.Any(t => t.Contains("Ask/Bid")), "Incomplete direction suppresses candidate and explains the data issue");
        inputs[1].Betweens = 0; Reload();
        Check(indicator.ZoneSnapshot.Length == 1, "Valid data after a reload restores the expected signal");
        indicator.EntryZonePercent = 100; Draw();
        Check(context.Fills.Count == 0 && context.Lines.Count == 1, "Zero-height zones draw as a line");
        indicator.EntryZonePercent = 50;

        // Test the actual ValueDataSeries seed, closed-bar replacement and live horizontal hold.
        inputs.Clear(); candles.Clear();
        for (var i = 0; i < 25; i++)
        {
            var c = new Candle { Open = 100 + i, Close = 100 + i, High = 101 + i, Low = 99 + i,
                Ask = 500, Bid = 500, Time = start.AddSeconds(i * 30), LastTime = start.AddSeconds(i * 30 + 20) };
            inputs.Add(c); candles.Add(Wrap(c));
        }
        Reload();
        var series = (ValueDataSeries)indicator.DataSeries[0];
        Check(series[20] == 0 && series[21] == 121 && series[22] > 121 && series[24] == series[23],
            "ATAS EMA series has exact warm-up and holds last closed value on live bar");
        var held = series[24]; inputs[24].Close = 140; inputs[24].High = 141;
        for (var i = 0; i < 1000; i++) Calculate(24);
        Check(series[24] == held && indicator.ClosedBarCount == 24, "Live EMA ignores repeated intrabar price changes");
        indicator.ShowEma = false;
        Check(series.VisualType == VisualMode.Hide && !series.ShowCurrentValue, "EMA visibility setting hides line and price label");
        indicator.ShowEma = true;
        Check(series.VisualType == VisualMode.Line, "EMA visibility can be restored without recalculation");
        candles.Clear(); Reload(); Draw();
        Check(indicator.ClosedBarCount == 0 && indicator.ZoneSnapshot.Length == 0 && series.Count == 0, "Empty-history reload removes old signals and EMA");
    }
}

public class Proxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Make<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public static object? Default(Type type) => type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
}
