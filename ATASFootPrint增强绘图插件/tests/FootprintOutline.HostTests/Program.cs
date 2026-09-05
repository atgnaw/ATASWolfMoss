using System.IO;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.FootprintOutline;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        var directory = Environment.GetEnvironmentVariable("ATAS_INSTALL_DIR") ?? @"C:\Program Files (x86)\ATAS Platform";
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try { return Run(); }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var passed=0;
        void Check(bool ok, string name) { if(!ok) throw new Exception(name); passed++; Console.WriteLine("PASS "+name); }
        var reads=0;
        var levels = new List<PriceVolumeInfo> {
            new() { Price=100m, Volume=200m }, new() { Price=100.25m, Volume=500m },
            new() { Price=100.5m, Volume=1500m }
        };
        var priceProvider = Proxy.Make<ISupportedPriceInfo>((m,a) => m.Name == "GetAllPriceLevels" ? ReadLevels() : Proxy.Default(m.ReturnType));
        IEnumerable<PriceVolumeInfo> ReadLevels() { reads++; return levels; }
        var parent=Proxy.Make<IIntCandle>((m,a)=>Proxy.Default(m.ReturnType));
        var candles=new List<IndicatorCandle> { new(priceProvider,parent,0.25m) };
        var creator=Proxy.Make<ICandleCreator>((m,a)=>m.Name switch {
            "get_Candles"=>candles, "get_Name"=>"Outline host test", "get_TickSize"=>0.25m, _=>Proxy.Default(m.ReturnType)
        });
        var source=new CandlePartSeries(creator,DataSeriesType.Close);
        var chartMode=ChartVisualModes.Clusters;
        var footprintMode=FootprintVisualModes.VolumeHistogram;
        var barWidth=100m;var rowHeight=6m;var yShift=0;var first=0;
        var container=Proxy.Make<IChartContainer>((m,a)=>m.Name switch {
            "get_Region"=>new Rectangle(20,20,700,500),
            "get_High"=>120m+yShift, "get_Low"=>90m+yShift, "get_Step"=>0.25m,
            "get_PriceRowHeight"=>rowHeight, "get_BarsWidth"=>barWidth, "get_BarSpacing"=>10m,
            "get_FirstVisibleBarNumber"=>first, "get_LastVisibleBarNumber"=>candles.Count-1,
            "get_TotalBars"=>candles.Count, "get_GetVisibleBarsCount"=>candles.Count,
            "GetXByBar"=>100+(int)a![0]!*110,
            "GetYByPrice"=>300-(int)(((decimal)a![0]!-100m)/0.25m*rowHeight)+yShift,
            _=>Proxy.Default(m.ReturnType)
        });
        var font=new RenderFont("Arial",10);
        var chart=Proxy.Make<IChart>((m,a)=>m.Name switch {
            "get_ChartVisualMode"=>chartMode, "get_FootprintVisualMode"=>footprintMode,
            "get_PriceChartContainer"=>container,"get_PriceAxisFont"=>font,
            _=>Proxy.Default(m.ReturnType)
        });
        var online=Proxy.Make<IOnlineDataProvider>((m,a)=>Proxy.Default(m.ReturnType));
        var dataProvider=Proxy.Make<IIndicatorDataProvider>((m,a)=>m.Name switch {
            "get_ChartInfo"=>chart,"get_OnlineDataProvider"=>online,
            "get_CandlesDataSeries"=>new ObservableCollection<CandlePartSeries>{source},
            "get_Panels"=>new ObservableCollection<string>{"Chart"},
            _=>Proxy.Default(m.ReturnType)
        });
        using var indicator=new FootprintOutlineIndicator();
        indicator.DataProvider=dataProvider;
        indicator.SourceDataSeries=source;
        var render=typeof(FootprintOutlineIndicator).GetMethod("OnRender",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var calculate=typeof(FootprintOutlineIndicator).GetMethod("OnCalculate",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var context=new RecordingRenderContext();
        void Draw() { context.Paths.Clear();context.Texts.Clear();render.Invoke(indicator,new object[]{context,DrawingLayouts.Final}); }
        Draw();
        Check(context.Paths.Count==0&&context.Texts.Count==1,"Unconfigured indicator shows one prompt");
        indicator.CustomProportion=1000;
        Draw();
        Check(context.Paths.Count==1,"Real Indicator + CandlePartSeries produce a closed contour");
        var initialPath=context.Paths[0];
        Check(initialPath[0]==initialPath[^1]&&initialPath.Min(p=>p.X)==110&&initialPath.Max(p=>p.X)==199,"Native integer origin and saturated width");
        Check(context.ClipBounds==new Rectangle(0,0,800,600)&&context.UsedClips.Last()==new Rectangle(20,20,700,500),"Chart clip applied and restored");
        var baselineReads=reads;
        for(var i=0;i<1000;i++) Draw();
        Check(reads==baselineReads&&ReferenceEquals(initialPath,context.Paths[0]),"1000 mouse repaints reuse geometry without rereading clusters");
        levels[1].Volume=900m;calculate.Invoke(indicator,new object[]{0,0m});Draw();
        Check(reads==baselineReads+1&&!ReferenceEquals(initialPath,context.Paths[0]),"Current candle refresh replaces cached geometry");
        var currentPath=context.Paths[0];barWidth=150m;Draw();
        Check(!ReferenceEquals(currentPath,context.Paths[0])&&context.Paths[0].Max(p=>p.X)==249,"Horizontal zoom rebuilds against native bar width");
        currentPath=context.Paths[0];rowHeight=8.5m;yShift=7;Draw();
        Check(!ReferenceEquals(currentPath,context.Paths[0]),"Vertical zoom and scrolling invalidate geometry");
        chartMode=ChartVisualModes.Candles;Draw();Check(context.Paths.Count==0,"Hidden on candlesticks");
        chartMode=ChartVisualModes.Clusters;footprintMode=FootprintVisualModes.DeltaHistogram;Draw();Check(context.Paths.Count==0,"Hidden on Delta Profile");
        footprintMode=FootprintVisualModes.VolumeHistogram;indicator.OutlineEnabled=false;Draw();Check(context.Paths.Count==0,"Disabled indicator draws nothing");
        indicator.OutlineEnabled=true;
        context.ThrowOnDraw=true;
        try { Draw(); throw new Exception("Expected rendering failure"); } catch(TargetInvocationException) { }
        context.ThrowOnDraw=false;
        Check(context.ClipBounds==new Rectangle(0,0,800,600),"Clip restored after render failure");
        candles.Clear();typeof(BaseIndicator).GetMethod("RecalculateValues",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(indicator,null);Draw();Check(context.Paths.Count==0,"History reload clears obsolete candles");
        candles.Add(new IndicatorCandle(priceProvider,parent,0.25m));typeof(BaseIndicator).GetMethod("RecalculateValues",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(indicator,null);Draw();Check(context.Paths.Count==1,"History reload repopulates snapshots");
        candles.Add(new IndicatorCandle(priceProvider,parent,0.25m));
        typeof(BaseIndicator).GetMethod("RecalculateValues",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(indicator,null);
        Draw();
        var retainedPath=context.Paths[1];
        first=1;Draw();
        Check(context.Paths.Count==1&&ReferenceEquals(retainedPath,context.Paths[0]),"Horizontal scroll retains overlapping bar geometry");
        first=2;Draw();Check(context.Paths.Count==0,"Empty visible range is handled");

        var previewPath=Environment.GetEnvironmentVariable("FOOTPRINT_PREVIEW_PATH");
        if(!string.IsNullOrWhiteSpace(previewPath))
        {
            first=0;barWidth=240;rowHeight=12;yShift=140;levels.Clear();
            candles.RemoveAt(1);
            var volumes=new decimal[]{30,40,120,320,180,450,800,500,270,90,180,460,1200,850,400,210,110,80,260,650,950,550,300,130,170,400,650,290,150,70,100,60};
            for(var i=0;i<volumes.Length;i++) levels.Add(new PriceVolumeInfo{Price=100+i*0.25m,Volume=volumes[i]});
            typeof(BaseIndicator).GetMethod("RecalculateValues",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(indicator,null);
            Draw();
            using var bitmap=new Bitmap(800,600);
            using var graphics=Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(21,24,38));
            using var titleFont=new Font("Arial",16);
            using var smallFont=new Font("Arial",10);
            using var titleBrush=new SolidBrush(Color.FromArgb(216,222,233));
            graphics.DrawString("Footprint Outline 1.0.1",titleFont,titleBrush,24,16);
            graphics.DrawString("SYNTHETIC DATA - host API test, not a live ATAS screenshot",smallFont,titleBrush,24,550);
            graphics.DrawString("Custom proportion: 1000 | bar width: 240 | marker: auto",smallFont,titleBrush,24,573);
            using var bid=new SolidBrush(Color.FromArgb(174,42,60));
            using var ask=new SolidBrush(Color.FromArgb(49,91,188));
            using var marker=new SolidBrush(Color.FromArgb(0,151,131));
            graphics.FillRectangle(marker,100,69,22,372);
            for(var i=0;i<volumes.Length;i++)
            {
                var width=Math.Max(1,(int)Math.Min(215m*volumes[i]/1000m,215m));
                graphics.FillRectangle(i%4==1?bid:ask,124,441-i*12,width,12);
            }
            using var linePen=new Pen(Color.FromArgb(216,222,233),1);
            foreach(var path in context.Paths) graphics.DrawLines(linePen,path);
            graphics.DrawString("Closed staircase outline",smallFont,titleBrush,400,190);
            graphics.DrawString("No internal row dividers",smallFont,titleBrush,400,220);
            graphics.DrawString("Volumes above 1000 are capped",smallFont,titleBrush,400,250);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(previewPath))!);
            bitmap.Save(previewPath,System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("Preview: "+previewPath);
        }

        Console.WriteLine($"RESULT {passed} host integration checks passed using ATAS {typeof(Indicator).Assembly.GetName().Version}");
        return 0;
    }
}
public class Proxy : DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Handler=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args);
    public static T Make<T>(Func<MethodInfo,object?[]?,object?> handler) where T:class {
        var proxy=Create<T,Proxy>();((Proxy)(object)proxy).Handler=handler;return proxy;
    }
    public static object? Default(Type t)=>t==typeof(void)?null:t.IsValueType?Activator.CreateInstance(t):null;
}

