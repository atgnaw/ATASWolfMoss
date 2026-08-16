namespace WolfMoss.ATAS.PriceMapping.Core;

public static class TickGenerator
{
    private static readonly decimal[] NiceMultipliers = [1m, 2m, 2.5m, 5m, 10m];

    public static decimal CalculateNiceStep(
        decimal minimum,
        decimal maximum,
        int pixelHeight,
        int minimumPixelSpacing = 36)
    {
        var range = Math.Abs(maximum - minimum);

        if (range <= 0m || pixelHeight <= 0)
            return 1m;

        var targetTickCount = Math.Max(2, pixelHeight / Math.Max(12, minimumPixelSpacing));
        var rawStep = range / targetTickCount;
        var exponent = (int)Math.Floor(Math.Log10((double)rawStep));
        var magnitude = Pow10(exponent);
        var normalized = rawStep / magnitude;

        foreach (var multiplier in NiceMultipliers)
        {
            if (normalized <= multiplier)
                return multiplier * magnitude;
        }

        return 10m * magnitude;
    }

    public static IEnumerable<decimal> EnumerateTicks(
        decimal minimum,
        decimal maximum,
        decimal step,
        int maximumCount = 200)
    {
        if (step <= 0m)
            yield break;

        if (maximum < minimum)
            (minimum, maximum) = (maximum, minimum);

        var first = Math.Ceiling(minimum / step) * step;
        var count = 0;

        for (var value = first; value <= maximum && count < maximumCount; value += step)
        {
            yield return value;
            count++;
        }
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;

        if (exponent >= 0)
        {
            for (var i = 0; i < exponent; i++)
                result *= 10m;
        }
        else
        {
            for (var i = 0; i > exponent; i--)
                result /= 10m;
        }

        return result;
    }
}
