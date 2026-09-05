namespace WolfMoss.ATAS.FootprintOutline;

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using global::ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using WolfMoss.ATAS.FootprintOutline.Core;
using MediaColor = System.Windows.Media.Color;

[DisplayName("Footprint Outline / 成交量外轮廓")]
[Category("WolfMoss")]
[Description("为 Volume Profile 叠加闭合阶梯外轮廓。ATAS 必须使用自定义比例，并与指标中的数值一致。")]
public sealed class FootprintOutlineIndicator : Indicator
{
    private sealed record Settings
    {
        public bool Enabled { get; init; } = true;
        public decimal Proportion { get; init; }
        public Color Color { get; init; } = Color.FromArgb(255, 216, 222, 233);
        public int LineWidth { get; init; } = 1;
        public int Offset { get; init; }
        public decimal WidthPercent { get; init; } = 100m;
        public bool DirectionMarker { get; init; } = true;
        public int MarkerWidth { get; init; }
    }

    private readonly record struct VerticalViewport(int Top, int Height, decimal High, decimal Low,
        decimal Step, decimal RowHeight);
    private readonly record struct ShapeSettings(decimal Proportion, int Offset, decimal WidthPercent,
        bool DirectionMarker, int MarkerWidth);
    private readonly record struct PenSettings(Color Color, int Width);
    private sealed record RenderedBar(long Revision, ProfileLayout Layout, Point[][] Paths);
    private readonly SnapshotStore _snapshots = new();
    private readonly object _settingsGate = new();
    private readonly object _renderGate = new();
    private readonly Dictionary<int, RenderedBar> _geometry = new();
    private readonly List<KeyValuePair<int, BarSnapshot>> _visibleSnapshots = new();
    private Settings _settings = new();
    private ShapeSettings? _shapeSettings;
    private PenSettings? _penSettings;
    private VerticalViewport? _verticalViewport;
    private RenderPen? _pen;
    private int _cacheFirst;
    private int _cacheLast = -1;
    private int _disposed;

    public FootprintOutlineIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = new ValueDataSeries("Hidden")
        {
            VisualType = VisualMode.Hide, IsHidden = true, ScaleIt = false,
            ShowCurrentValue = false, ShowZeroValue = false
        };
        EnableCustomDrawing = true;
        DrawAbovePrice = true;
        // Final paints once over the native bars on every chart repaint. Data and
        // viewport revisions keep mouse-only repaints from rebuilding geometry.
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(Name = "启用轮廓", GroupName = "轮廓", Order = 10)]
    public bool OutlineEnabled { get => ReadSettings().Enabled; set => Change(s => s with { Enabled = value }); }

    [Display(Name = "自定义比例值", GroupName = "轮廓", Order = 20,
        Description = "必须与 ATAS Footprint → 比例 → 自定义比例中的数值相同。修改 ATAS 后请同步修改这里；0 表示未配置。")]
    [Range(typeof(decimal), "0", "1000000000000000000")]
    public decimal CustomProportion
    {
        get => ReadSettings().Proportion;
        set => Change(s => s with { Proportion = Math.Clamp(value, 0m, 1_000_000_000_000_000_000m) });
    }

    [Display(Name = "轮廓颜色", GroupName = "轮廓", Order = 30)]
    public MediaColor OutlineColor
    {
        get { var c = ReadSettings().Color; return MediaColor.FromArgb(c.A, c.R, c.G, c.B); }
        set => Change(s => s with { Color = Color.FromArgb(value.A, value.R, value.G, value.B) });
    }

    [Display(Name = "线宽（像素）", GroupName = "轮廓", Order = 40)]
    [Range(1, 8)]
    public int LineWidth { get => ReadSettings().LineWidth; set => Change(s => s with { LineWidth = Math.Clamp(value, 1, 8) }); }

    [Display(Name = "显示方向指示条", GroupName = "与 ATAS 布局匹配", Order = 50,
        Description = "与 ATAS 的 Show Direction Indicator/Marker 保持一致；它会影响横条可用宽度。")]
    public bool ShowDirectionMarker { get => ReadSettings().DirectionMarker; set => Change(s => s with { DirectionMarker = value }); }

    [Display(Name = "方向指示条宽度", GroupName = "与 ATAS 布局匹配", Order = 60,
        Description = "与 ATAS 的 Direction Indicator Width 保持一致；0 表示使用 ATAS 自动宽度规则。Additional Footprint 和 Draw Borders 请关闭。")]
    [Range(0, 200)]
    public int DirectionMarkerWidth { get => ReadSettings().MarkerWidth; set => Change(s => s with { MarkerWidth = Math.Clamp(value, 0, 200) }); }

    [Display(Name = "横向偏移（像素）", GroupName = "微调", Order = 70)]
    [Range(-200, 200)]
    public int HorizontalOffset { get => ReadSettings().Offset; set => Change(s => s with { Offset = Math.Clamp(value, -200, 200) }); }

    [Display(Name = "宽度修正（%）", GroupName = "微调", Order = 80,
        Description = "默认 100%。先匹配比例和方向指示条参数，再根据需要微调。")]
    [Range(typeof(decimal), "10", "200")]
    public decimal WidthPercent { get => ReadSettings().WidthPercent; set => Change(s => s with { WidthPercent = Math.Clamp(value, 10m, 200m) }); }

    private Settings ReadSettings() => Volatile.Read(ref _settings);

    private void Change(Func<Settings, Settings> update)
    {
        lock (_settingsGate)
            Volatile.Write(ref _settings, update(_settings));
        if (Volatile.Read(ref _disposed) == 0)
            RedrawChart();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (Volatile.Read(ref _disposed) != 0 || bar < 0 || bar >= CurrentBar)
            return;
        var generation = _snapshots.Generation;
        var candle = GetCandle(bar);
        var levels = candle.GetAllPriceLevels();
        // The chart's CandleCreator already applies its tick aggregation and origin.
        // Copy its displayed prices verbatim; regrouping raw ticks here would shift bins.
        PriceLevel[] copied;
        if (levels is null)
            copied = [];
        else
        {
            var capacity = levels.TryGetNonEnumeratedCount(out var count) ? count : 0;
            var numericLevels = new List<PriceLevel>(capacity);
            foreach (var level in levels)
                if (level is not null && level.Volume > 0)
                    numericLevels.Add(new(level.Price, level.Volume));
            copied = ProfileGeometry.CopyLevelsInPlace(numericLevels);
        }
        var step = ChartInfo?.PriceChartContainer.Step ?? InstrumentInfo?.TickSize ?? 0m;
        _snapshots.Set(bar, CurrentBar, step, copied, generation);
    }

    protected override void OnRecalculate()
    {
        _snapshots.Clear();
        lock (_renderGate) { ClearGeometry(); _verticalViewport = null; }
        base.OnRecalculate();
    }

    protected override void OnFinishRecalculate()
    {
        base.OnFinishRecalculate();
        RedrawChart();
    }

    protected override void OnDataProviderChanged(IIndicatorDataProvider? oldDataProvider, IIndicatorDataProvider? newDataProvider)
    {
        _snapshots.Clear();
        lock (_renderGate) { ClearGeometry(); _verticalViewport = null; }
        base.OnDataProviderChanged(oldDataProvider, newDataProvider);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var chart = ChartInfo;
        var settings = ReadSettings();
        if (Volatile.Read(ref _disposed) != 0 || chart is null || !settings.Enabled
            || chart.ChartVisualMode != ChartVisualModes.Clusters
            || chart.FootprintVisualMode != FootprintVisualModes.VolumeHistogram)
            return;

        var container = chart.PriceChartContainer;
        var region = Rectangle.Intersect(container.Region, context.ClipBounds);
        if (region.Width <= 0 || region.Height <= 0)
            return;
        var savedClip = context.ClipBounds;
        context.SetClip(region);
        try
        {
            if (settings.Proportion <= 0)
            {
                context.DrawString("Footprint Outline：请填写与 ATAS 相同的自定义比例值", chart.PriceAxisFont,
                    settings.Color, region.Left + 8, region.Top + 24);
                return;
            }
            var first = Math.Max(0, FirstVisibleBarNumber);
            var last = Math.Min(LastVisibleBarNumber, CurrentBar - 1);
            if (last < first || container.Step <= 0 || container.PriceRowHeight <= 0)
                return;

            var viewport = new VerticalViewport(container.Region.Top, container.Region.Height,
                container.High, container.Low, container.Step, container.PriceRowHeight);
            var shapeSettings = new ShapeSettings(settings.Proportion, settings.Offset, settings.WidthPercent,
                settings.DirectionMarker, settings.MarkerWidth);
            var penSettings = new PenSettings(settings.Color, settings.LineWidth);
            lock (_renderGate)
            {
                if (_verticalViewport != viewport || _shapeSettings != shapeSettings)
                {
                    ClearGeometry();
                    _verticalViewport = viewport;
                    _shapeSettings = shapeSettings;
                    _cacheFirst = first;
                    _cacheLast = last;
                }
                else if (_cacheFirst != first || _cacheLast != last)
                {
                    foreach (var (bar, _) in _geometry)
                        if (bar < first || bar > last)
                            _geometry.Remove(bar);
                    _cacheFirst = first;
                    _cacheLast = last;
                }
                if (_pen is null || _penSettings != penSettings)
                {
                    _penSettings = penSettings;
                    _pen = new RenderPen(settings.Color, settings.LineWidth);
                }
                _snapshots.CopyVisible(first, last, _visibleSnapshots);
                foreach (var (bar, snapshot) in _visibleSnapshots)
                {
                    if (snapshot.Step != container.Step)
                        continue; // Await recalculation instead of showing stale aggregation.
                    var profileLayout = ProfileGeometry.GetLayout(container.GetXByBar(bar), container.BarsWidth,
                        settings.DirectionMarker, settings.MarkerWidth, settings.Offset, settings.WidthPercent);
                    if (!_geometry.TryGetValue(bar, out var cached) || cached.Revision != snapshot.Revision || cached.Layout != profileLayout)
                    {
                        var rectangles = ProfileGeometry.GetRectangles(snapshot.Levels, snapshot.Step,
                            container.PriceRowHeight, profileLayout, settings.Proportion,
                            price => container.GetYByPrice(price, true));
                        cached = new(snapshot.Revision, profileLayout, ProfileGeometry.TraceOutline(rectangles));
                        _geometry[bar] = cached;
                    }
                    foreach (var path in cached.Paths)
                        context.DrawLines(_pen!, path);
                }
            }
        }
        finally { context.SetClip(savedClip); }
    }

    protected override void OnDispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _snapshots.Clear();
        lock (_renderGate) { ClearGeometry(); _visibleSnapshots.Clear(); _pen = null; }
        base.OnDispose();
    }

    private void ClearGeometry()
    {
        _geometry.Clear();
        _cacheFirst = 0;
        _cacheLast = -1;
    }
}
