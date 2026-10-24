using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;

namespace WolfMoss.ATAS.DrawingSync;

[DisplayName("Drawing Sync / 微型合约绘图同步")]
[Category("WolfMoss")]
[Description("ATAS 8.0.15.304 原生水平线、射线、矩形和趋势线同步。支持同组多张大合约和微型合约图，各添加一个实例并开启。")]
public sealed class DrawingSyncIndicator : Indicator
{
    private readonly Guid _id = Guid.NewGuid();
    private readonly System.Threading.Timer _timer;
    private volatile bool _enabled;
    private string _group = "默认";
    private string _status = "未启用：各图设置相同同步组，再开启同步";
    private int _disposed;
    private int _pending;
    private int _revision;
    private NativeBridge? _bridge;
    private int _bridgeRevision = -1;

    public DrawingSyncIndicator() : base(true)
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
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _timer = new(_ => Schedule(), null, 500, 300);
    }

    [Display(Name = "启用原生绘图同步", GroupName = "同步", Order = 10,
        Description = "仅同步 Global 主图水平线、射线、矩形和趋势线。取消 Global 会保留当前本地绘图、删除另一侧镜像并解除配对。")]
    public bool SyncEnabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; ResetSession(); }
    }

    [Display(Name = "同步组", GroupName = "同步", Order = 20,
        Description = "各图填写相同名称，支持多张大合约和多张微型合约。同族全局线只允许一个组和合约类型同时同步。")]
    public string SyncGroup
    {
        get => Volatile.Read(ref _group);
        set { var group = (value ?? "").Trim(); if (group == SyncGroup) return; Volatile.Write(ref _group, group); ResetSession(); }
    }

    [Display(Name = "状态文字纵向位置", GroupName = "显示", Order = 30)]
    [Range(0, 1000)]
    public int StatusOffset { get; set; } = 48;

    [Display(Name = "显示状态文字", GroupName = "显示", Order = 31)]
    public bool ShowStatus { get; set; } = true;

    [ReadOnly(true)]
    [Display(Name = "当前状态", GroupName = "诊断", Order = 40)]
    public string SyncStatus => Volatile.Read(ref _status);

    private void ResetSession()
    {
        Interlocked.Increment(ref _revision);
        // A closing chart may have dropped a queued GUI callback.
        Volatile.Write(ref _pending, 0);
        SyncHub.Shared.Remove(_id);
        SetStatus(_enabled ? "准备连接原生图表" : "未启用：各图设置相同同步组，再开启同步");
    }

    private void SetStatus(string status)
    {
        if (Interlocked.Exchange(ref _status, status) == status) return;
        try { if (Volatile.Read(ref _disposed) == 0) RedrawChart(); } catch { /* Chart may be closing. */ }
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0 || !_enabled) return;
        var revision = Volatile.Read(ref _revision);
        var ticket = revision + 1;
        if (Interlocked.CompareExchange(ref _pending, ticket, 0) != 0) return;
        try
        {
            var chart = ChartInfo;
            if (chart is null) { Interlocked.CompareExchange(ref _pending, 0, ticket); return; }
            var group = SyncGroup;
            var core = NativeBridge.FindCore(chart);
            NativeBridge.Post(core, () =>
            {
                try
                {
                    bool Active() => Volatile.Read(ref _disposed) == 0 && _enabled && revision == Volatile.Read(ref _revision) && ReferenceEquals(chart, ChartInfo);
                    if (!Active()) return;
                    if (group.Length is 0 or > 64 || group.Any(char.IsControl) || group.Contains('|'))
                        throw new InvalidOperationException("同步组需为 1–64 字符，不能包含换行或 |。");
                    if (_bridge is null || _bridgeRevision != revision || !ReferenceEquals(_bridge.Core, core))
                    {
                        SyncHub.Shared.Remove(_id);
                        _bridge = new(core);
                        _bridgeRevision = revision;
                    }
                    SyncHub.Shared.Tick(new(_id, _bridge, group, Active, SetStatus, () => RedrawChart()));
                }
                catch (Exception ex)
                {
                    SyncHub.Shared.Remove(_id);
                    SetStatus("暂停：" + ex.GetBaseException().Message);
                }
                finally { Interlocked.CompareExchange(ref _pending, 0, ticket); }
            });
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _pending, 0, ticket);
            SyncHub.Shared.Remove(_id);
            SetStatus("暂停：" + ex.GetBaseException().Message);
        }
    }

    protected override void OnCalculate(int bar, decimal value) { }

    protected override void OnDataProviderChanged(IIndicatorDataProvider? oldDataProvider, IIndicatorDataProvider? newDataProvider)
    {
        ResetSession();
        base.OnDataProviderChanged(oldDataProvider, newDataProvider);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var chart = ChartInfo;
        if (!ShowStatus || chart is null || Volatile.Read(ref _disposed) != 0) return;
        var region = chart.PriceChartContainer.Region;
        context.DrawString("Drawing Sync 1.0.0 | " + Volatile.Read(ref _status), chart.PriceAxisFont,
            _enabled ? Color.Gold : Color.Gray, region.Left + 8, region.Top + Math.Clamp(StatusOffset, 0, 1000));
    }

    protected override void OnDispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer.Dispose();
        SyncHub.Shared.Remove(_id);
        _bridge = null;
        base.OnDispose();
    }
}



