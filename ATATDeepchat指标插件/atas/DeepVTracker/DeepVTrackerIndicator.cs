using System.ComponentModel;
using System.Globalization;
using ATAS.Indicators;

namespace WolfMoss.ATAS.DeepVTracker;

[DisplayName("Deep-V Tracker / 成交流追踪")]
[Category("WolfMoss")]
[Description("逐笔成交加速、成交量/记录数衰竭、可变减速标记及吸收/压力线。需要完整逐笔历史；数据不匹配的柱会明确提示并重新预热。")]
public sealed partial class DeepVTrackerIndicator : Indicator
{
    private readonly object _gate = new();
    private readonly Queue<TradeTick> _live = new();
    private TrackerConfig _config = new();
    private TradeReplay? _replay;
    private CumulativeTradesRequest? _request;
    private CumulativeTradesRequest? _inFlight;
    private List<ChartBar> _history = [];
    private bool _ready, _disposed, _faulted;
    private int _startupIndex, _observed = -1;
    private long _captureTime, _lastLiveTime;
    private decimal _tickSize;
    private string? _identity;
    private string _status = "等待图表数据";
    private const int MaxBufferedTrades = 1_000_000;

    public DeepVTrackerIndicator() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = new ValueDataSeries("DeepVTracker.Hidden") { VisualType = VisualMode.Hide, IsHidden = true, ScaleIt = false, ShowCurrentValue = false };
        EnableCustomDrawing = true; DrawAbovePrice = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        CumulativeTradesRequest? send = null;
        lock (_gate)
        {
            if (_disposed || bar < 0 || bar >= CurrentBar) return;
            var identity = $"{InstrumentInfo?.Instrument}|{InstrumentInfo?.TickSize}|{ChartInfo?.ChartType}|{ChartInfo?.TimeFrame}";
            if (CurrentBar - 1 < _observed || (_identity is not null && identity != _identity)) ResetState();
            _identity = identity; _observed = Math.Max(_observed, bar);
            if (bar != CurrentBar - 1 || _faulted) return;
            try
            {
                if (_replay is null) send = PrepareHistory();
                else if (_ready) DrainLive();
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
            { Fail(ex.Message); }
        }
        if (send is not null) SendHistory();
    }

    private void SendHistory()
    {
        lock (_gate)
        {
            if (_disposed || _faulted || _inFlight is not null || _request is not { } send) return;
            _inFlight = send;
            try { RequestForCumulativeTrades(send); }
            catch (Exception ex)
            {
                _inFlight = null;
                    if (_request?.RequestId == send.RequestId)
                    {
                        _request = null; _ready = true;
                        foreach (var historical in _history) _replay!.Missing(historical.Flow, "历史逐笔请求失败");
                        _history.Clear(); _status = "历史逐笔请求失败；等待后续完整实时柱。" + ex.Message;
                    }
            }
        }
    }

    private CumulativeTradesRequest? PrepareHistory()
    {
        _tickSize = InstrumentInfo?.TickSize ?? 0;
        if (_tickSize <= 0) { _status = "等待合约 TickSize"; return null; }
        var range = ResolveRange();
        _startupIndex = CurrentBar - 1;
        var first = Math.Max(0, _startupIndex - _historyBars);
        _captureTime = GetCandle(_startupIndex).Time.Ticks;
        _history = new(_startupIndex - first);
        for (var i = first; i < _startupIndex; i++) _history.Add(ReadBar(i, true));
        _replay = new(_config, range, first);
        if (_history.Count == 0) { _ready = true; _status = "等待下一根完整实时柱"; return null; }
        // History ends before the attachment bar. Live capture starts at the following full bar.
        // This explicit gap avoids guessing duplicate executions at an asynchronous history/live seam.
        var begin = new DateTime(_history[0].Flow.Time);
        var end = new DateTime(_captureTime).AddTicks(-1);
        if (end < begin)
        {
            foreach (var bar in _history) _replay.Missing(bar.Flow, "历史/实时边界时间重叠");
            _history.Clear(); _ready = true; _status = "边界柱等待后续完整逐笔"; return null;
        }
        _request = new CumulativeTradesRequest(begin, end, CumulativeTradesMode.Filter, 0, 0);
        _status = $"正在加载 {_history.Count} 根柱的原始逐笔历史";
        return _request;
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        lock (_gate)
        {
            if (_inFlight?.RequestId == request.RequestId) _inFlight = null;
            if (_disposed) return;
            if (_request?.RequestId != request.RequestId || _replay is null)
            {
                SendHistory();
                return;
            }
            try
            {
                var ticks = new List<TradeTick>();
                foreach (var group in cumulativeTrades)
                {
                    if (group.Ticks is null) continue;
                    foreach (var tick in group.Ticks)
                        if (tick.Time >= request.BeginTime && tick.Time <= request.EndTime)
                            ticks.Add(ConvertTick(tick));
                }
                // Stable ordering preserves distinct identical prints; never deduplicate by price/time/size.
                var sorted = ticks.OrderBy(t => t.Time).ToArray();
                _replay.History(_history, sorted);
                _ready = true; _request = null; _history.Clear();
                _status = "历史已处理；接入时未收柱跳过，实时从后续完整柱开始";
                DrainLive();
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
            { Fail("历史逐笔：" + ex.Message); }
        }
        RedrawChart();
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        lock (_gate)
        {
            if (_disposed || _faulted || _replay is null || trade.Time.Ticks < _captureTime) return;
            try
            {
                var tick = ConvertTick(trade);
                if (_lastLiveTime != 0 && tick.Time < _lastLiveTime) throw new ArgumentException("实时逐笔时间倒序，请重新计算指标");
                if (_live.Count >= MaxBufferedTrades) throw new InvalidOperationException("逐笔等待队列已满，请缩短历史范围并重载");
                _lastLiveTime = tick.Time; _live.Enqueue(tick);
                if (_ready) DrainLive();
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
            { Fail(ex.Message); }
        }
        RedrawChart();
    }

    private void DrainLive()
    {
        if (_replay is null || !_ready || _faulted) return;
        // CurrentBar can advance before OnCalculate. Read the actual chart, never infer a bar from elapsed time.
        var latest = CurrentBar - 1;
        _observed = Math.Max(_observed, latest);
        while (_replay.NextIndex <= latest)
        {
            var i = _replay.NextIndex;
            var chart = ReadBar(i, i < latest);
            if (i <= _startupIndex)
            {
                if (i == latest) return;
                _captureTime = Math.Max(_captureTime, chart.LastTime + 1);
                _replay.Missing(chart.Flow, "接入时的未完整捕获柱，已重新预热");
                continue;
            }
            if (chart.Flow.Time < _captureTime)
            {
                if (i == latest) return;
                _replay.Missing(chart.Flow, "历史/实时交界同一时间戳，无法唯一归属");
                continue;
            }
            while (_live.Count > 0 && _live.Peek().Time < chart.Flow.Time) _live.Dequeue();
            var advanced = false;
            while (_live.Count > 0)
            {
                var tick = _live.Peek();
                if (tick.Time > chart.LastTime)
                {
                    if (i < latest && _replay.Volume != chart.Flow.Volume)
                    { _replay.Missing(chart.Flow, "实时逐笔缺失或在此柱开始前才接入"); advanced = true; }
                    break; // trade can precede its OnCalculate update; leave it queued
                }
                if (_replay.Volume == chart.Flow.Volume) break;
                if (tick.Volume > chart.Flow.Volume - _replay.Volume)
                {
                    if (i < latest)
                    {
                        while (_live.Count > 0 && _live.Peek().Time <= chart.LastTime) _live.Dequeue();
                        _replay.Missing(chart.Flow, "成交被跨柱分拆或累计量不匹配"); advanced = true;
                    }
                    break;
                }
                _replay.Tick(chart, tick); _live.Dequeue();
            }
            if (advanced) continue;
            if (i < latest && _replay.Volume == chart.Flow.Volume)
            { _replay.Close(chart); continue; }
            return;
        }
    }

    private TradeTick ConvertTick(MarketDataArg tick)
    {
        if (tick.Time == default) throw new ArgumentException("逐笔缺少时间");
        var volume = Integer(tick.Volume);
        if (volume == 0) throw new ArgumentException("逐笔成交量必须大于零");
        return new(tick.Time.Ticks, Price(tick.Price), volume,
            tick.Direction == TradeDirection.Buy ? TradeSide.Buy : tick.Direction == TradeDirection.Sell ? TradeSide.Sell : TradeSide.Unknown);
    }
    private ChartBar ReadBar(int index, bool profile)
    {
        var c = GetCandle(index);
        if (c.Time == default || c.LastTime < c.Time) throw new ArgumentException("柱时间缺失或倒序");
        var ask = Integer(c.Ask); var bid = Integer(c.Bid); var volume = Integer(c.Volume);
        if (ask + bid > volume) throw new ArgumentException("柱 Ask/Bid 超过总量");
        var levels = profile ? (c.GetAllPriceLevels() ?? []).Where(p => p.Volume > 0)
            .Select(p => new PriceLevel(Price(p.Price), Integer(p.Ask), Integer(p.Bid), Integer(p.Volume))).OrderBy(p => p.Price).ToArray() : [];
        var bar = new FlowBar(index, c.Time.Ticks, Math.Max(c.Time.Ticks + 1, c.LastTime.Ticks + 1),
            Price(c.Open), Price(c.High), Price(c.Low), Price(c.Close), ask, bid, volume, 0, 0, levels,
            ask + bid == volume && c.Betweens == 0, IsNewSession(index));
        return new(bar, c.LastTime.Ticks, Integer(c.Ticks));
    }
    private long Price(decimal price)
    {
        var ticks = price / _tickSize;
        if (decimal.Truncate(ticks) != ticks || ticks < long.MinValue || ticks > long.MaxValue) throw new ArgumentException("价格不在合约 tick 网格");
        return (long)ticks;
    }
    private static long Integer(decimal value)
    {
        if (value < 0 || value > long.MaxValue || decimal.Truncate(value) != value) throw new ArgumentException("需要非负整数成交量和笔数；不支持小数手数");
        return (long)value;
    }
    private long ResolveRange()
    {
        if (_rangeMode == RangeRuleMode.Other) return 0;
        if (_rangeMode == RangeRuleMode.Range) return _rangeTicks;
        if (!string.Equals(ChartInfo?.ChartType, "Range", StringComparison.OrdinalIgnoreCase)) return 0;
        if (long.TryParse(ChartInfo?.TimeFrame, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 && value <= 1_000_000) return value;
        throw new ArgumentException("无法识别 Range 周期，请将区间规则改为 Range 并填写真实 tick 数");
    }
    private void Fail(string reason)
    {
        _replay?.AbortPreview();
        if (_replay is not null)
            foreach (var (id, signal) in _replay.Signals.ToArray())
                if (signal.Active) _replay.Signals[id] = signal with { Active = false, End = Math.Max(signal.Start, _replay.NextIndex - 1) };
        _faulted = true; _request = null; _live.Clear();
        _status = "输入暂停：" + reason;
    }
    private void ResetState()
    {
        _replay = null; _request = null; _history.Clear(); _live.Clear(); _ready = _faulted = false;
        _observed = -1; _identity = null; _captureTime = _lastLiveTime = 0; _status = "等待逐笔历史";
    }
    protected override void OnRecalculate() { lock (_gate) ResetState(); base.OnRecalculate(); }
    protected override void OnDataProviderChanged(IIndicatorDataProvider? oldDataProvider, IIndicatorDataProvider? newDataProvider)
    { lock (_gate) ResetState(); base.OnDataProviderChanged(oldDataProvider, newDataProvider); }
    protected override void OnDispose() { lock (_gate) { _disposed = true; ResetState(); } base.OnDispose(); }
    internal Signal[] SignalSnapshot { get { lock (_gate) return _replay?.Signals.Values.ToArray() ?? []; } }
    internal string Status { get { lock (_gate) return _status; } }
    internal int NextIndex { get { lock (_gate) return _replay?.NextIndex ?? -1; } }
}
