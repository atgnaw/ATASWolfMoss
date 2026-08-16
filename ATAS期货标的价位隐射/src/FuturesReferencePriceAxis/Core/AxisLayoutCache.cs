namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public readonly record struct AxisLayoutKey(
    decimal VisibleFuturesLow,
    decimal VisibleFuturesHigh,
    decimal Ratio,
    int Width,
    int Height,
    int ReferenceDigits);

public readonly record struct AxisTickLayout(
    decimal ReferencePrice,
    decimal FuturesPrice,
    string Text);

public sealed class AxisLayoutCache
{
    private AxisLayoutKey? _key;
    private IReadOnlyList<AxisTickLayout> _layout = Array.Empty<AxisTickLayout>();

    public int BuildCount { get; private set; }

    public IReadOnlyList<AxisTickLayout> GetOrCreate(AxisLayoutKey key)
    {
        if (_key == key)
            return _layout;

        var low = MappingMath.ToReferencePrice(key.VisibleFuturesLow, key.Ratio);
        var high = MappingMath.ToReferencePrice(key.VisibleFuturesHigh, key.Ratio);
        var step = TickGenerator.CalculateNiceStep(low, high, key.Height);
        var format = $"F{Math.Clamp(key.ReferenceDigits, 0, 8)}";
        _layout = TickGenerator
            .EnumerateTicks(low, high, step)
            .Select(referencePrice => new AxisTickLayout(
                referencePrice,
                MappingMath.ToFuturesPrice(referencePrice, key.Ratio),
                referencePrice.ToString(format, CultureInfo.InvariantCulture)))
            .ToArray();
        _key = key;
        BuildCount++;
        return _layout;
    }

    public void Invalidate()
    {
        _key = null;
        _layout = Array.Empty<AxisTickLayout>();
    }
}
