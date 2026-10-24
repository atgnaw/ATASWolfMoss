namespace WolfMoss.ATAS.DeepVTracker;

internal sealed record ChartBar(FlowBar Flow, long LastTime, long TickCount);

internal sealed class BarAccumulator
{
    private readonly Dictionary<long, PriceLevel> _profile = [];
    private FlowBar _bar;
    public long LastTime { get; private set; }
    public long Count => _bar.AskCount + _bar.BidCount + _unknownCount;
    private long _unknownCount;
    public FlowBar Current => _bar;
    public BarAccumulator(FlowBar chart, TradeTick first)
    {
        _bar = chart with { Open = first.Price, High = first.Price, Low = first.Price, Close = first.Price,
            Ask = 0, Bid = 0, Volume = 0, AskCount = 0, BidCount = 0, Profile = [], EndTime = first.Time + 1 };
    }
    public FlowBar Add(TradeTick tick)
    {
        if (LastTime != 0 && tick.Time < LastTime) throw new ArgumentException("逐笔时间倒序");
        LastTime = tick.Time;
        var ask = tick.Side == TradeSide.Buy ? tick.Volume : 0;
        var bid = tick.Side == TradeSide.Sell ? tick.Volume : 0;
        if (tick.Side == TradeSide.Unknown) _unknownCount++;
        var p = _profile.GetValueOrDefault(tick.Price);
        _profile[tick.Price] = new(tick.Price, p.Ask + ask, p.Bid + bid, p.Total + tick.Volume);
        return _bar = _bar with { High = Math.Max(_bar.High, tick.Price), Low = Math.Min(_bar.Low, tick.Price), Close = tick.Price,
            Ask = _bar.Ask + ask, Bid = _bar.Bid + bid, Volume = _bar.Volume + tick.Volume,
            AskCount = _bar.AskCount + (ask > 0 ? 1 : 0), BidCount = _bar.BidCount + (bid > 0 ? 1 : 0),
            EndTime = Math.Max(_bar.Time + 1, tick.Time + 1), Known = _bar.Known && tick.Side != TradeSide.Unknown };
    }
    public FlowBar Complete() => _bar with { Profile = _profile.Values.OrderBy(p => p.Price).ToArray() };
    public bool Matches(ChartBar chart)
    {
        var c = chart.Flow;
        if (_bar.Volume != c.Volume || _bar.Ask != c.Ask || _bar.Bid != c.Bid
            || _bar.Open != c.Open || _bar.High != c.High || _bar.Low != c.Low || _bar.Close != c.Close
            || (chart.TickCount > 0 && Count != chart.TickCount)) return false;
        // Verify the full footprint, not only the totals: raw prints must belong to this exact chart bar.
        if (_profile.Count != c.Profile.Length) return false;
        foreach (var level in c.Profile)
            if (!_profile.TryGetValue(level.Price, out var p) || p != level) return false;
        return true;
    }
}

/// <summary>
/// A bar transaction. Live output is previewed, then committed only after OHLC/volume/footprint
/// reconciliation. Rejected bars restore the detector checkpoint and all changed drawings.
/// </summary>
internal sealed class TradeReplay
{
    private TrackerEngine _engine;
    private TrackerEngine? _checkpoint;
    private readonly Dictionary<long, Signal?> _undo = [];
    private BarAccumulator? _accumulator;
    public Dictionary<long, Signal> Signals { get; } = [];
    public Dictionary<int, string> Issues { get; } = [];
    public int NextIndex { get; private set; }
    public long Volume => _accumulator?.Current.Volume ?? 0;
    public long LastTickTime => _accumulator?.LastTime ?? 0;
    public TradeReplay(TrackerConfig config, long range, int first) { _engine = new(config, range); NextIndex = first; }
    private void Publish(Signal s)
    {
        if (_checkpoint is not null && !_undo.ContainsKey(s.Id)) _undo.Add(s.Id, Signals.GetValueOrDefault(s.Id));
        if (s.Visible) Signals[s.Id] = s; else Signals.Remove(s.Id);
    }
    public void Tick(ChartBar chart, TradeTick tick)
    {
        if (chart.Flow.Index != NextIndex) throw new InvalidOperationException("Trade/bar index mismatch.");
        if (_accumulator is null)
        {
            _checkpoint = _engine.Clone();
            _accumulator = new(chart.Flow, tick);
            _engine.Open(_accumulator.Current, Publish);
        }
        var current = _accumulator.Add(tick);
        _engine.Trade(tick, current, Publish);
        _engine.End(current, Publish);
    }
    public bool Close(ChartBar chart)
    {
        if (_accumulator is null || !_accumulator.Matches(chart))
        {
            Missing(chart.Flow, "逐笔与柱的 OHLC、量、笔数或分价数据不一致"); return false;
        }
        var completed = _accumulator.Complete();
        _engine.Close(completed, Publish);
        _checkpoint = null; _undo.Clear(); _accumulator = null; NextIndex++;
        if (!completed.Known) Issues[completed.Index] = "主动方向缺失：仅保留总量加速检测";
        return true;
    }
    public void Missing(FlowBar bar, string reason)
    {
        if (bar.Index != NextIndex) throw new InvalidOperationException("Missing-bar index mismatch.");
        AbortPreview();
        _engine.MissingBar(bar, Publish); NextIndex++; Issues[bar.Index] = reason;
    }
    public void AbortPreview()
    {
        if (_checkpoint is not null)
        {
            _engine = _checkpoint;
            foreach (var (id, old) in _undo) { if (old is null) Signals.Remove(id); else Signals[id] = old; }
        }
        _checkpoint = null; _undo.Clear(); _accumulator = null;
    }
    public void History(IReadOnlyList<ChartBar> bars, IReadOnlyList<TradeTick> ticks)
    {
        var cursor = 0;
        foreach (var bar in bars)
        {
            while (cursor < ticks.Count && ticks[cursor].Time < bar.Flow.Time) cursor++;
            var invalid = false;
            while (cursor < ticks.Count && ticks[cursor].Time <= bar.LastTime && Volume < bar.Flow.Volume)
            {
                var tick = ticks[cursor];
                if (tick.Volume > bar.Flow.Volume - Volume) { invalid = true; break; }
                Tick(bar, tick); cursor++;
            }
            if (invalid)
            {
                // A print split across synthetic bars cannot be reconstructed as independent trades.
                while (cursor < ticks.Count && ticks[cursor].Time <= bar.LastTime) cursor++;
                Missing(bar.Flow, "一笔成交跨柱分拆或历史逐笔不完整");
            }
            else Close(bar);
        }
    }
}
