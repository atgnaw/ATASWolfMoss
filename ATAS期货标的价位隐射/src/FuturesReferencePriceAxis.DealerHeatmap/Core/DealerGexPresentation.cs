namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public readonly record struct DealerGexRgb(byte Red, byte Green, byte Blue);

public static class DealerGexPresentation
{
    private static readonly DealerGexRgb Negative = new(224, 74, 82);
    private static readonly DealerGexRgb Positive = new(47, 158, 101);
    private static readonly DealerGexRgb King = new(245, 247, 250);
    private static readonly DealerGexRgb Neutral = new(112, 121, 133);

    public static decimal GetFillRatio(decimal value, decimal maximumAbsolute)
        => maximumAbsolute <= 0m
            ? 0m
            : Math.Clamp(Math.Abs(value) / maximumAbsolute, 0m, 1m);

    public static DealerGexRgb GetNodeColor(decimal value, string nodeType)
    {
        if (string.Equals(nodeType, "king", StringComparison.OrdinalIgnoreCase))
            return King;

        if (value < 0m)
            return Negative;

        return value > 0m ? Positive : Neutral;
    }

    public static bool UseDarkText(string nodeType)
        => string.Equals(nodeType, "king", StringComparison.OrdinalIgnoreCase);

    public static string FormatStrike(decimal strike)
        => strike.ToString("0.##", CultureInfo.InvariantCulture);

    public static IReadOnlyList<int> ResolveLabelTops(
        IReadOnlyList<int> lineCenters,
        int minimumTop,
        int maximumBottom,
        int labelHeight,
        int spacing)
    {
        if (lineCenters.Count == 0)
            return Array.Empty<int>();

        var indexed = lineCenters
            .Select(static (center, index) => (Center: center, Index: index))
            .OrderBy(static value => value.Center)
            .ToArray();
        var resolved = new int[lineCenters.Count];
        var nextTop = minimumTop;

        foreach (var value in indexed)
        {
            var desired = value.Center - labelHeight / 2;
            var top = Math.Max(desired, nextTop);
            resolved[value.Index] = top;
            nextTop = top + labelHeight + spacing;
        }

        var overflow = nextTop - spacing - maximumBottom;

        if (overflow > 0)
        {
            for (var index = 0; index < resolved.Length; index++)
                resolved[index] -= overflow;
        }

        var underflow = minimumTop - resolved.Min();

        if (underflow > 0)
        {
            for (var index = 0; index < resolved.Length; index++)
                resolved[index] += underflow;
        }

        return resolved;
    }
}
