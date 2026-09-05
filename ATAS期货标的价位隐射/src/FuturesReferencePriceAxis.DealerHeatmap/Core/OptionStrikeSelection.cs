namespace WolfMoss.ATAS.PriceMapping.Core;

public static class OptionStrikeSelection
{
    public static int NormalizeLevelCount(int value)
    {
        var clamped = Math.Clamp(value, 5, 21);
        return clamped % 2 == 0 ? Math.Min(21, clamped + 1) : clamped;
    }

    public static IReadOnlyList<decimal> SelectCentered(
        IEnumerable<decimal> availableStrikes,
        decimal mappedSpot,
        int requestedLevels,
        out decimal atmStrike)
        => SelectCenteredCandidates(
            availableStrikes,
            mappedSpot,
            NormalizeLevelCount(requestedLevels),
            out atmStrike);

    public static IReadOnlyList<decimal> SelectCenteredCandidates(
        IEnumerable<decimal> availableStrikes,
        decimal mappedSpot,
        int maximumCandidates,
        out decimal atmStrike)
    {
        var ordered = availableStrikes
            .Where(static strike => strike > 0m)
            .Distinct()
            .OrderBy(static strike => strike)
            .ToArray();

        if (ordered.Length == 0 || mappedSpot <= 0m)
        {
            atmStrike = 0m;
            return Array.Empty<decimal>();
        }

        var levels = Math.Clamp(maximumCandidates, 1, ordered.Length);

        var atmIndex = 0;
        var bestDistance = decimal.MaxValue;

        for (var index = 0; index < ordered.Length; index++)
        {
            var distance = Math.Abs(ordered[index] - mappedSpot);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                atmIndex = index;
            }
        }

        atmStrike = ordered[atmIndex];
        var half = levels / 2;
        var start = Math.Max(0, atmIndex - half);
        var endExclusive = Math.Min(ordered.Length, start + levels);
        start = Math.Max(0, endExclusive - levels);
        return ordered[start..endExclusive];
    }

    public static IReadOnlyList<decimal> ApplySymmetricBudget(
        IReadOnlyList<decimal> centeredStrikes,
        decimal atmStrike,
        int availableMarketDataLines)
    {
        var strikeBudget = Math.Max(0, availableMarketDataLines / 2);

        if (strikeBudget <= 0 || centeredStrikes.Count == 0)
            return Array.Empty<decimal>();

        if (strikeBudget >= centeredStrikes.Count)
            return centeredStrikes.ToArray();

        if (strikeBudget % 2 == 0)
            strikeBudget--;

        strikeBudget = Math.Max(1, strikeBudget);
        return SelectCentered(centeredStrikes, atmStrike, strikeBudget, out _);
    }

    public static (decimal Lower, decimal Upper) GetRowBounds(
        IReadOnlyList<decimal> orderedStrikes,
        int index)
    {
        if (orderedStrikes.Count == 0 || index < 0 || index >= orderedStrikes.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var strike = orderedStrikes[index];
        var lowerDistance = index > 0
            ? strike - orderedStrikes[index - 1]
            : index + 1 < orderedStrikes.Count
                ? orderedStrikes[index + 1] - strike
                : 1m;
        var upperDistance = index + 1 < orderedStrikes.Count
            ? orderedStrikes[index + 1] - strike
            : lowerDistance;
        return (
            strike - Math.Max(0.0001m, lowerDistance) / 2m,
            strike + Math.Max(0.0001m, upperDistance) / 2m);
    }
}
