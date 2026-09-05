namespace WolfMoss.ATAS.PriceMapping.Core;

using System.Globalization;

public readonly record struct HeatmapRgb(byte Red, byte Green, byte Blue);

public static class DealerHeatmapPresentation
{
    private static readonly HeatmapRgb Negative = new(75, 13, 91);
    private static readonly HeatmapRgb Neutral = new(216, 92, 98);
    private static readonly HeatmapRgb Positive = new(255, 230, 0);

    public static decimal GetStrikeStep(string ticker)
        => string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase)
            ? 5m
            : 1m;

    public static (decimal Lower, decimal Upper) GetReferenceBounds(
        string ticker,
        decimal strike)
    {
        var halfStep = GetStrikeStep(ticker) / 2m;
        return (strike - halfStep, strike + halfStep);
    }

    public static (decimal Lower, decimal Upper) GetAdaptiveReferenceBounds(
        string ticker,
        decimal strike,
        decimal? lowerNeighbor,
        decimal? upperNeighbor)
    {
        var nominalHalfStep = GetStrikeStep(ticker) / 2m;
        var lowerHalfStep = GetNeighborHalfStep(
            nominalHalfStep,
            lowerNeighbor.HasValue ? strike - lowerNeighbor.Value : null);
        var upperHalfStep = GetNeighborHalfStep(
            nominalHalfStep,
            upperNeighbor.HasValue ? upperNeighbor.Value - strike : null);

        return (strike - lowerHalfStep, strike + upperHalfStep);
    }

    public static HeatmapRgb GetColor(
        decimal value,
        decimal minimum,
        decimal maximum)
    {
        if (value < 0m)
        {
            var denominator = Math.Abs(Math.Min(minimum, -1m));
            var amount = 1m - Math.Clamp(Math.Abs(value) / denominator, 0m, 1m);
            return Interpolate(Negative, Neutral, amount);
        }

        if (value > 0m)
        {
            var denominator = Math.Max(maximum, 1m);
            var amount = Math.Clamp(value / denominator, 0m, 1m);
            return Interpolate(Neutral, Positive, amount);
        }

        return Neutral;
    }

    public static string FormatGex(decimal value)
    {
        var absolute = Math.Abs(value);
        var prefix = value < 0m ? "-" : string.Empty;

        if (absolute >= 1_000_000_000m)
            return prefix + FormatScaled(absolute / 1_000_000_000m) + "B";

        if (absolute >= 1_000_000m)
            return prefix + FormatScaled(absolute / 1_000_000m) + "M";

        if (absolute >= 1_000m)
            return prefix + FormatScaled(absolute / 1_000m) + "K";

        return prefix + absolute.ToString("#,0.##", CultureInfo.InvariantCulture);
    }

    public static bool UseDarkText(HeatmapRgb color)
    {
        var luminance = 0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue;
        return luminance >= 150d;
    }

    private static string FormatScaled(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static decimal GetNeighborHalfStep(decimal nominalHalfStep, decimal? distance)
    {
        if (!distance.HasValue || distance.Value <= 0m)
            return nominalHalfStep;

        return Math.Min(nominalHalfStep, distance.Value / 2m);
    }

    private static HeatmapRgb Interpolate(
        HeatmapRgb start,
        HeatmapRgb end,
        decimal amount)
    {
        var value = (double)Math.Clamp(amount, 0m, 1m);
        return new HeatmapRgb(
            (byte)Math.Round(start.Red + (end.Red - start.Red) * value),
            (byte)Math.Round(start.Green + (end.Green - start.Green) * value),
            (byte)Math.Round(start.Blue + (end.Blue - start.Blue) * value));
    }
}
