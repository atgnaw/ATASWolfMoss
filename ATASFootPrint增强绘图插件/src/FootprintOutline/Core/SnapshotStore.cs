namespace WolfMoss.ATAS.FootprintOutline.Core;

internal sealed record BarSnapshot(long Revision, decimal Step, PriceLevel[] Levels);

/// <summary>Copies numeric values; never retains mutable ATAS PriceVolumeInfo objects.</summary>
internal sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<int, BarSnapshot> _bars = new();
    private int _totalBars;
    private long _revision;
    private long _generation;

    public long Generation { get { lock (_gate) return _generation; } }
    public int Count { get { lock (_gate) return _bars.Count; } }

    public bool Set(int bar, int totalBars, decimal step, PriceLevel[] levels, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || bar < 0 || bar >= totalBars)
                return false;
            if (totalBars < _totalBars)
                TrimFrom(totalBars);
            _totalBars = totalBars;
            if (_bars.TryGetValue(bar, out var current)
                && current.Step == step
                && current.Levels.AsSpan().SequenceEqual(levels))
                return false;
            _bars[bar] = new(++_revision, step, levels);
            return true;
        }
    }

    public void CopyVisible(int first, int last, List<KeyValuePair<int, BarSnapshot>> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            destination.Clear();
            var start = Math.Max(0, first);
            var end = Math.Min(last, _totalBars - 1);
            if (end >= start)
                destination.EnsureCapacity(end - start + 1);
            for (var bar = start; bar <= end; bar++)
                if (_bars.TryGetValue(bar, out var snapshot))
                    destination.Add(new(bar, snapshot));
        }
    }

    public KeyValuePair<int, BarSnapshot>[] GetVisible(int first, int last)
    {
        var result = new List<KeyValuePair<int, BarSnapshot>>();
        CopyVisible(first, last, result);
        return result.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _bars.Clear();
            _totalBars = 0;
            _generation++;
        }
    }

    private void TrimFrom(int firstRemoved)
    {
        // Dictionary enumeration supports removing the current entry on modern .NET.
        foreach (var (bar, _) in _bars)
            if (bar >= firstRemoved)
                _bars.Remove(bar);
    }
}
