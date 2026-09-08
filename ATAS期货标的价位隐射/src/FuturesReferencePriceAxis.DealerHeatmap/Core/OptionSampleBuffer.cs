namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Collections;

// Data-owner-only deque: expiration advances a head instead of moving the window.
internal sealed class OptionSampleBuffer : IReadOnlyList<OptionCumulativeSample>
{
    private OptionCumulativeSample[] _items = Array.Empty<OptionCumulativeSample>();
    private int _head;
    public int Count { get; private set; }
    public OptionCumulativeSample this[int index]
        => (uint)index < (uint)Count ? _items[(_head + index) % _items.Length]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public void Add(OptionCumulativeSample sample)
    {
        if (Count == _items.Length)
        {
            var grown = new OptionCumulativeSample[Math.Max(16, _items.Length * 2)];
            for (var i = 0; i < Count; i++) grown[i] = this[i];
            _items = grown;
            _head = 0;
        }
        _items[(_head + Count++) % _items.Length] = sample;
    }
    public void RemovePrefix(int count)
    {
        if ((uint)count > (uint)Count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        _head = (_head + count) % _items.Length;
        Count -= count;
    }
    public void Clear() { _head = 0; Count = 0; }
    public IEnumerator<OptionCumulativeSample> GetEnumerator()
    { for (var i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
