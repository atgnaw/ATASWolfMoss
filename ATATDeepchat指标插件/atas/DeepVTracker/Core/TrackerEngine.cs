namespace WolfMoss.ATAS.DeepVTracker;

/// <summary>Detectors ported from the C++ core. Single caller; clone only at bar boundaries.</summary>
internal sealed class TrackerEngine(TrackerConfig config, long range)
{
    private Acceleration _acceleration = new();
    private Exhaustion _volume = new(), _count = new();
    private Slowdown _slowdown = new();
    private Levels _levels = new();
    private Dictionary<long, Signal> _active = [];
    private FlowBar? _lastClosed;
    private FlowBar? _accelerationClosed;
    private bool _gap;
    private int? _open;
    public int? OpenIndex => _open;
    public TrackerEngine Clone()
    {
        var c = (TrackerEngine)MemberwiseClone(); c._acceleration = _acceleration.Clone(); c._volume = _volume.Clone();
        c._count = _count.Clone(); c._slowdown = _slowdown.Clone(); c._levels = _levels.Clone(); c._active = new(_active); return c;
    }
    public void Open(FlowBar bar, Action<Signal> emit)
    {
        if (_open is not null || (_lastClosed is not null && bar.Index != _lastClosed.Index + 1)) throw new InvalidOperationException("Nonconsecutive bar open.");
        _open = bar.Index; _gap = false; _acceleration.Open();
        if (bar.NewSession && config.EnableSlowdown) _slowdown.ResetRange(bar.Close, config.Slowdown);
        if (!bar.Known) BreakFlow(emit);
    }
    private void Require(FlowBar bar)
    {
        if (_open != bar.Index) throw new InvalidOperationException("Event does not belong to the open bar.");
    }
    private void Publish(Signal? signal, Action<Signal> emit)
    {
        if (signal is null) return;
        if (signal.Active) _active[signal.Id] = signal;
        emit(signal);
    }
    private void BreakFlow(Action<Signal> emit)
    {
        if (_gap) return;
        if (_slowdown.Marker is { Visible: true } marker) emit(marker with { Visible = false });
        _gap = true; _volume = _volume.Reset(); _count = _count.Reset(); _slowdown = _slowdown.Reset(); _levels = _levels.Reset();
    }
    public void Trade(TradeTick tick, FlowBar current, Action<Signal> emit)
    {
        Require(current);
        if (tick.Volume <= 0) throw new ArgumentException("Nonpositive tick volume.");
        if (tick.Side == TradeSide.Unknown || !current.Known) BreakFlow(emit);
        if (config.EnableAcceleration)
        {
            var signal = _acceleration.Trade(tick, current.Index, config.Acceleration);
            if (signal is not null)
            {
                var registration = signal.Created == current.Index ? current : _accelerationClosed;
                if (registration is null || registration.Index != signal.Created) throw new InvalidOperationException("No matching acceleration registration bar.");
                var above = registration.Close < signal.Upper && signal.Up;
                Publish(signal with { StopsAbove = above, Stop = above ? signal.Upper : signal.Lower, Ask = signal.Up }, emit);
            }
        }
        if (config.EnableSlowdown && !_gap) _slowdown.Trade(tick, range == 0 ? 8 : range * 2, config.Slowdown);
    }
    public void End(FlowBar current, Action<Signal> emit)
    {
        Require(current);
        if (config.EnableSlowdown && !_gap) Publish(_slowdown.End(current, _levels.Visibility(range)), emit);
    }
    public void Close(FlowBar bar, Action<Signal> emit)
    {
        Require(bar);
        if (!bar.Known) BreakFlow(emit);
        foreach (var pair in _active.ToArray())
        {
            var old = pair.Value;
            var crossed = old.StopsAbove ? Math.Max(bar.Open, bar.Close) > old.Stop : Math.Min(bar.Open, bar.Close) < old.Stop;
            var next = old with { End = bar.Index, Active = !crossed && bar.Index - old.Created < old.Lifetime };
            if (next.Active) _active[pair.Key] = next; else _active.Remove(pair.Key);
            emit(next);
        }
        if (!_gap)
        {
            if (config.EnableExhaustion) { Publish(_volume.Close(bar, config.Exhaustion, false), emit); Publish(_count.Close(bar, config.Exhaustion, true), emit); }
            if (config.EnableSlowdown) Publish(_slowdown.Evaluate(bar, _levels.Visibility(range)), emit);
            if (config.EnableLevels || config.EnableSlowdown)
            {
                var result = _levels.Close(bar, config, range);
                foreach (var signal in result.Signals) Publish(signal, emit);
                if (config.EnableSlowdown && result.Reveal) Publish(_slowdown.Reveal(), emit);
            }
        }
        _lastClosed = bar with { Profile = [] }; _open = null;
        if (_acceleration.LastBar == bar.Index) _accelerationClosed = _lastClosed;
    }
    // Adapter-only discontinuity: missing raw trades must also reset the one-second detector.
    // This is distinct from a known-volume trade with Unknown aggressor, which still feeds acceleration.
    public void MissingBar(FlowBar bar, Action<Signal> emit)
    {
        if (_open is null) Open(bar with { Known = false }, emit);
        BreakFlow(emit); _acceleration = _acceleration.Reset();
        Close(bar with { Known = false }, emit);
    }
}
