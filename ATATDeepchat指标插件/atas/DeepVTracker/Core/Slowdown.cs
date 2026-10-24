namespace WolfMoss.ATAS.DeepVTracker;

internal sealed class Slowdown
{
    private Samples _maxima = new(35);
    // Sparse storage, but evaluation explicitly includes untraded price levels as zero.
    private Dictionary<long, PriceLevel> _levels = [];
    private long _low, _high, _maximum, _threshold, _lowCeiling, _highFloor, _previousExtreme, _sequence;
    private bool _initialized, _current, _dirty = true;
    public Signal? Marker { get; private set; }
    public Slowdown Clone() { var c = (Slowdown)MemberwiseClone(); c._levels = new(_levels); c._maxima = _maxima.Clone(); return c; }
    public Slowdown Reset() => new() { _sequence = _sequence };
    public void ResetRange(long price, SignalStrength strength)
    {
        if (_initialized)
        {
            _maxima.Add(_maximum);
            var k = strength == SignalStrength.Weak ? .45 : strength == SignalStrength.Strong ? 1.05 : .8;
            var value = _maxima.Mean - _maxima.Deviation * k;
            if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue) throw new OverflowException("Slowdown threshold exceeds Int32.");
            _threshold = (long)value;
        }
        _initialized = true; _levels.Clear(); _low = _high = _lowCeiling = _highFloor = price;
        _maximum = _previousExtreme = 0; _current = false;
    }
    public void Trade(TradeTick tick, long limit, SignalStrength strength)
    {
        var low = Math.Min(_low, tick.Price); var high = Math.Max(_high, tick.Price);
        if (!_initialized || high - low > limit) ResetRange(tick.Price, strength);
        else
        {
            if (high - low > 3)
            {
                if (low != _low) _lowCeiling = low + 3;
                if (high != _high) _highFloor = high - 3;
            }
            _low = low; _high = high;
        }
        if (tick.Side == TradeSide.Unknown) return;
        var row = _levels.GetValueOrDefault(tick.Price);
        var ask = row.Ask + (tick.Side == TradeSide.Buy ? tick.Volume : 0);
        var bid = row.Bid + (tick.Side == TradeSide.Sell ? tick.Volume : 0);
        _levels[tick.Price] = new(tick.Price, ask, bid, ask + bid);
        _maximum = Math.Max(_maximum, Math.Max(ask, bid));
        _dirty |= tick.Side == TradeSide.Buy ? tick.Price >= _highFloor : tick.Price <= _lowCeiling;
    }
    private bool Matches(bool up)
    {
        long prior = 0;
        for (var i = 0; i < 3; i++)
        {
            var row = _levels.GetValueOrDefault(up ? _low + i : _high - i);
            var value = up ? row.Bid : row.Ask;
            if (value < prior || value > _threshold) return false;
            prior = value;
        }
        return true;
    }
    public Signal? Evaluate(FlowBar bar, Visibility visibility)
    {
        if (!_initialized || _high - _low < 3 || _threshold == 0) return null;
        bool? up = Matches(true) ? true : Matches(false) ? false : null;
        if (up is null)
        {
            if (!_current || Marker is not { Visible: true }) return null;
            return Marker = Marker with { Visible = false };
        }
        var extreme = up.Value ? _low : _high;
        var create = !_current && (Marker is null || Marker.Start + 1 != bar.Index);
        if (!create && _previousExtreme == extreme) return null; // also preserve old X and visibility
        if (create)
        {
            _current = true;
            Marker = new Signal { Id = Signal.Key(SignalKind.Slowdown, ++_sequence), Kind = SignalKind.Slowdown, Created = bar.Index };
        }
        _previousExtreme = extreme;
        return Marker = Marker! with { Start = bar.Index, End = bar.Index, Lower = extreme, Upper = extreme, Up = up.Value,
            Visible = visibility.Active || (bar.Index - visibility.End <= visibility.Lookback && extreme >= visibility.Low && extreme <= visibility.High) };
    }
    public Signal? End(FlowBar bar, Visibility visibility)
    {
        if (_initialized) { _lowCeiling = _low + 3; _highFloor = _high - 3; }
        if (!_dirty) return null;
        var result = Evaluate(bar, visibility); _dirty = false; return result;
    }
    public Signal? Reveal()
    {
        if (!_current || Marker is not { Visible: false }) return null;
        return Marker = Marker with { Visible = true };
    }
}
