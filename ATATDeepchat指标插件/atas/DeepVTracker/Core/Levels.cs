namespace WolfMoss.ATAS.DeepVTracker;

internal sealed class Levels
{
    private sealed record Node(FlowBar Bar, Node? Previous);
    private Node? _history;
    private Samples _trueRanges = new(21), _segmentVolumes = new(25), _segmentDeltas = new(25), _barVolumes = new(50);
    private bool _active;
    private int _start, _end, _nextCandidate;
    private long _bodyLow, _bodyHigh, _maintainedLow, _maintainedHigh, _volume, _delta, _sequence;
    public Visibility Visibility(long range) => new(_active, _end, range > 0 ? 4 : 5, _bodyLow, _bodyHigh);
    public Levels Reset() => new() { _sequence = _sequence };
    public Levels Clone()
    {
        var c = (Levels)MemberwiseClone();
        c._trueRanges = _trueRanges.Clone(); c._segmentVolumes = _segmentVolumes.Clone();
        c._segmentDeltas = _segmentDeltas.Clone(); c._barVolumes = _barVolumes.Clone(); return c;
    }

    public (Signal[] Signals, bool Reveal) Close(FlowBar bar, TrackerConfig config, long range)
    {
        var previousClose = _history?.Bar.Close ?? bar.Close;
        _history = new(bar, _history);
        if (range == 0) _trueRanges.Add(Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.Low - previousClose), Math.Abs(bar.High - previousClose))));
        var minimum = range > 0 ? 4 : 5;
        var maximum = range > 0 ? range + range / 2 : (long)Math.Max(1, _trueRanges.Mean * .85);
        var exit = range > 0 ? range / 4 : (long)(maximum / .85 * .2);
        if ((range == 0 && _trueRanges.Added <= 21) || bar.Index <= minimum) return ([], false);
        if (_active)
        {
            var upper = config.Absorption == SignalStrength.Strong ? bar.High : bar.Close;
            var lower = config.Absorption == SignalStrength.Strong ? bar.Low : bar.Close;
            if ((upper > _bodyHigh && upper - _bodyHigh > exit) || (lower < _bodyLow && _bodyLow - lower > exit))
            {
                var bars = new List<FlowBar>();
                for (var node = _history; node is not null && node.Bar.Index >= _start; node = node.Previous)
                    if (node.Bar.Index <= _end) bars.Add(node.Bar);
                bars.Reverse();
                _segmentVolumes.Add(_volume); _segmentDeltas.Add(_delta);
                foreach (var item in bars) _barVolumes.Add(item.Volume);
                var signals = PassesVolume(config, range) && config.EnableLevels ? Create(bar, config, bars) : [];
                _active = false;
                return (signals, false);
            }
            _maintainedLow = Math.Min(_maintainedLow, bar.Low); _maintainedHigh = Math.Max(_maintainedHigh, bar.High);
            _volume += bar.Volume; _delta += bar.Ask - bar.Bid; _end = bar.Index;
            return ([], false);
        }
        if (bar.Index < _nextCandidate) return ([], false);
        var bodyLow = Math.Min(bar.Open, bar.Close); var bodyHigh = Math.Max(bar.Open, bar.Close);
        var start = bar.Index; long volume = 0, delta = 0;
        // As in the source, history already includes the current bar.
        for (var node = _history; node is not null; node = node.Previous)
        {
            var low = Math.Min(bodyLow, Math.Min(node.Bar.Open, node.Bar.Close));
            var high = Math.Max(bodyHigh, Math.Max(node.Bar.Open, node.Bar.Close));
            if (high - low > maximum) { _nextCandidate = node.Bar.Index + minimum; break; }
            bodyLow = low; bodyHigh = high; start = node.Bar.Index;
            volume += node.Bar.Ask + node.Bar.Bid; delta += node.Bar.Ask - node.Bar.Bid;
        }
        _start = start;
        if (bar.Index - start + 1 < minimum) return ([], false);
        _active = true; _bodyLow = _maintainedLow = bodyLow; _bodyHigh = _maintainedHigh = bodyHigh;
        _volume = volume; _delta = delta; _end = bar.Index;
        return ([], true);
    }

    private bool PassesVolume(TrackerConfig config, long range)
    {
        var strength = config.Absorption;
        var barK = strength == SignalStrength.Weak ? (range > 0 ? .7 : -.5)
            : strength == SignalStrength.Medium ? (range > 0 ? 1 : -.3) : (range > 0 ? 1.3 : 0);
        var segmentK = strength == SignalStrength.Weak ? .7 : strength == SignalStrength.Medium ? 1 : 1.3;
        var deltaK = strength == SignalStrength.Weak ? .3 : strength == SignalStrength.Medium ? .5 : .8;
        return Math.Abs(_volume) >= _barVolumes.Threshold(barK) * (_end - _start + 1)
            || Math.Abs(_volume) >= _segmentVolumes.Threshold(segmentK)
            || Math.Abs(_delta) >= _segmentDeltas.Threshold(deltaK);
    }

    private Signal[] Create(FlowBar bar, TrackerConfig config, List<FlowBar> bars)
    {
        var area = ProfileArea.Calculate(bars);
        var above = bar.High > _bodyHigh;
        var control = config.LevelMode == LevelPosition.Aggressive ? (above ? _bodyHigh : _bodyLow) : area.Poc;
        var extreme = config.LevelMode == LevelPosition.Conservative ? (above ? _maintainedLow : _maintainedHigh) : (above ? area.Low : area.High);
        _sequence++;
        Signal Make(SignalKind kind, long price) => new()
        {
            Id = Signal.Key(kind, _sequence), Kind = kind, Start = bar.Index, End = bar.Index, Created = bar.Index,
            Lower = price, Upper = price, Stop = price, Lifetime = config.ProjectionBars, Up = above,
            Ask = _delta >= 0, Absorption = above != (_delta >= 0), StopsAbove = bar.Close < price, Active = true
        };
        return [Make(SignalKind.Control, control), Make(SignalKind.Extreme, extreme)];
    }
}

internal readonly record struct ProfileArea(long Poc, long Low, long High)
{
    public static ProfileArea Calculate(IEnumerable<FlowBar> bars)
    {
        var volumeByPrice = new Dictionary<long, long>();
        long total = 0, maximum = 0, poc = 0;
        void Append(PriceLevel level)
        {
            var value = volumeByPrice.GetValueOrDefault(level.Price) + level.Total;
            volumeByPrice[level.Price] = value; total += level.Total;
            if (value > maximum) { maximum = value; poc = level.Price; }
        }
        foreach (var bar in bars)
        {
            var middle = 0;
            while (middle < bar.Profile.Length && bar.Profile[middle].Price < bar.Open) middle++;
            for (var i = middle; i < bar.Profile.Length; i++) Append(bar.Profile[i]);
            for (var i = middle - 1; i >= 0; i--) Append(bar.Profile[i]);
        }
        var upper = poc + 1; var lower = poc - 1; long accumulated = 0;
        if (volumeByPrice.Count != 0)
        {
            do
            {
                var hasAbove = volumeByPrice.TryGetValue(upper, out var above);
                var hasBelow = volumeByPrice.TryGetValue(lower, out var below);
                if (hasAbove && (!hasBelow || above > below)) { accumulated += above; upper++; }
                else { accumulated += below; lower--; }
                if (accumulated >= total * .7 || !(hasAbove || hasBelow)) break;
            } while (true);
        }
        return new(poc, lower + 1, upper - 1);
    }
}
