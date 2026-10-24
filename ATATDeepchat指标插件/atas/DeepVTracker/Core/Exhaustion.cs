namespace WolfMoss.ATAS.DeepVTracker;

internal sealed class Exhaustion
{
    private Queue<(long Volume, long Delta)> _window = new();
    private Samples _volumePeaks = new(10), _deltaPeaks = new(10);
    private long _rollingVolume, _rollingDelta, _peakVolume, _peakDelta, _sequence;
    private int _start; // retain CLR/source initialization: index 0, negative side
    private bool _positive;
    public Exhaustion Clone()
    {
        var c = (Exhaustion)MemberwiseClone(); c._window = new(_window);
        c._volumePeaks = _volumePeaks.Clone(); c._deltaPeaks = _deltaPeaks.Clone(); return c;
    }
    public Exhaustion Reset() => new() { _sequence = _sequence };
    public Signal? Close(FlowBar bar, SignalStrength strength, bool count)
    {
        var length = strength == SignalStrength.Weak ? 5 : strength == SignalStrength.Strong ? 10 : 8;
        var ask = count ? bar.AskCount : bar.Ask;
        var bid = count ? bar.BidCount : bar.Bid;
        var removed = _window.Count >= length ? _window.Dequeue() : default;
        _rollingVolume = _rollingVolume + ask + bid - removed.Volume;
        _rollingDelta = _rollingDelta + ask - bid - removed.Delta;
        _window.Enqueue((ask + bid, ask - bid));
        Signal? result = null;
        if ((_rollingDelta >= 0) != _positive)
        {
            if (bar.Index - _start >= length / 2)
            {
                _volumePeaks.Add(_peakVolume); _deltaPeaks.Add(_peakDelta);
                if (_peakVolume >= _volumePeaks.Threshold(.5)
                    && _peakDelta <= _deltaPeaks.Threshold(strength == SignalStrength.Weak ? 1.25 : 1))
                {
                    var kind = count ? SignalKind.ExhaustionCount : SignalKind.ExhaustionVolume;
                    var price = !_positive ? bar.Low - 3 : bar.High + 3;
                    result = new Signal { Id = Signal.Key(kind, ++_sequence), Kind = kind,
                        Start = bar.Index, End = bar.Index, Created = bar.Index, Lower = price, Upper = price, Up = !_positive };
                }
            }
            _start = bar.Index; _positive = _rollingDelta >= 0; _peakVolume = _peakDelta = 0;
        }
        _peakVolume = Math.Max(_peakVolume, _rollingVolume);
        _peakDelta = Math.Max(_peakDelta, Math.Abs(_rollingDelta));
        return result;
    }
}
