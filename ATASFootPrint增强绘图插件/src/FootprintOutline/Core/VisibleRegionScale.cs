namespace WolfMoss.ATAS.FootprintOutline.Core;

/// <summary>ATAS 8.0.14.397 / 8.0.15.304: nearest-rank percentile of positive per-candle maxima.
/// Includes all prices of horizontally visible candles, not just vertically clipped rows.
/// Owned exclusively by the indicator render lock.</summary>
internal sealed class VisibleRegionScale
{
    private readonly List<(int Bar, decimal Maximum)> _inputs = new();
    private readonly List<decimal> _ordered = new();
    private int _percentile;
    private decimal _value = 1m;

    public bool TryGetValue(List<KeyValuePair<int, BarSnapshot>> snapshots,
        int first, int last, decimal step, int percentile, out decimal value)
    {
        value = 1m;
        // Do not normalize a partially loaded viewport, or mix aggregation generations.
        if (last < first || snapshots.Count != (long)last - first + 1)
            return false;
        for (var i = 0; i < snapshots.Count; i++)
            if (snapshots[i].Key != first + i || snapshots[i].Value.Step != step)
                return false;

        percentile = Math.Clamp(percentile, 1, 100);
        var changed = _percentile != percentile || _inputs.Count != snapshots.Count;
        if (!changed)
            for (var i = 0; i < snapshots.Count; i++)
                if (_inputs[i] != (snapshots[i].Key, snapshots[i].Value.MaximumVolume))
                {
                    changed = true;
                    break;
                }
        if (changed)
        {
            _inputs.Clear();
            _ordered.Clear();
            _inputs.EnsureCapacity(snapshots.Count);
            _ordered.EnsureCapacity(snapshots.Count);
            var maximum = 1m;
            foreach (var (bar, snapshot) in snapshots)
            {
                _inputs.Add((bar, snapshot.MaximumVolume));
                if (snapshot.MaximumVolume > 0)
                    _ordered.Add(snapshot.MaximumVolume);
                maximum = Math.Max(maximum, snapshot.MaximumVolume);
            }
            _percentile = percentile;
            _value = maximum;
            if (percentile < 100 && _ordered.Count > 0)
            {
                _ordered.Sort();
                var rank = (int)(((long)_ordered.Count * percentile + 99) / 100) - 1;
                _value = Math.Max(1m, _ordered[rank]);
            }
        }
        value = _value;
        return true;
    }

    public void Clear()
    {
        _inputs.Clear();
        _ordered.Clear();
        _percentile = 0;
        _value = 1m;
    }
}
