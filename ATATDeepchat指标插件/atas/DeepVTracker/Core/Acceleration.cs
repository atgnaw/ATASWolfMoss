namespace WolfMoss.ATAS.DeepVTracker;

internal sealed class Acceleration
{
    private Samples _samples = new(50);
    private bool _initialized, _canSample;
    private long _deadline, _volume, _delta, _peakVolume, _peakDelta, _low, _high, _sequence;
    private int _first, _last;
    public int LastBar => _last;
    public void Open() => _canSample = true;
    public Acceleration Clone() { var c = (Acceleration)MemberwiseClone(); c._samples = _samples.Clone(); return c; }
    public Acceleration Reset() => new() { _sequence = _sequence };

    public Signal? Trade(TradeTick tick, int bar, SignalStrength strength)
    {
        Signal? result = null;
        if (!_initialized || tick.Time >= _deadline)
        {
            if (_initialized && _canSample && _peakVolume != 0)
            {
                _samples.Add(_peakVolume); // candidate first enters the statistics
                var k = strength == SignalStrength.Weak ? 3.25 : strength == SignalStrength.Strong ? 6.5 : 4.75;
                if (_peakVolume >= _samples.Threshold(k))
                    result = new Signal { Id = Signal.Key(SignalKind.Acceleration, ++_sequence), Kind = SignalKind.Acceleration,
                        Start = _first, End = _last, Created = _last, Lower = _low, Upper = _high,
                        Up = _peakDelta >= 0, Active = true, Lifetime = 12, StartOffset = .5 };
                _peakVolume = _peakDelta = 0; _canSample = false;
            }
            _deadline = tick.Time + TimeSpan.TicksPerSecond;
            // Intentional source compatibility: first bucket is initialized from price tick coordinate.
            _volume = _delta = _initialized ? 0 : tick.Price;
            _initialized = true; _low = _high = tick.Price; _first = _last = bar;
        }
        _volume += tick.Volume;
        _delta += tick.Side == TradeSide.Buy ? tick.Volume : tick.Side == TradeSide.Sell ? -tick.Volume : 0;
        _low = Math.Min(_low, tick.Price); _high = Math.Max(_high, tick.Price); _last = bar;
        if (_volume > _peakVolume) { _peakVolume = _volume; _peakDelta = _delta; }
        return result;
    }
}
