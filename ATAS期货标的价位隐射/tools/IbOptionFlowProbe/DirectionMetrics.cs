namespace IbOptionFlowProbe;

internal sealed record PressureResult(decimal Fraction, decimal Lower, decimal Upper, bool Passed);

internal sealed record DirectionMetrics(decimal Total, decimal? Tendency, decimal? Coverage,
    decimal? Lower, decimal? Upper, decimal? ErrorTolerance, PressureResult[] Pressure,
    string Direction, decimal? HighestPassedPressure)
{
    public static DirectionMetrics Calculate(decimal buy, decimal sell, decimal unknown, decimal mid = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(buy);
        ArgumentOutOfRangeException.ThrowIfNegative(sell);
        ArgumentOutOfRangeException.ThrowIfNegative(unknown);
        ArgumentOutOfRangeException.ThrowIfNegative(mid);

        var classified = buy + sell;
        var total = classified + unknown + mid;
        if (total == 0)
            return new(0, null, null, null, null, null, [], "NO_DATA", null);

        var net = buy - sell;
        var lower = (net - unknown) / total;
        var upper = (net + unknown) / total;
        // Normalize before doubling error mass or total, so representable totals cannot overflow.
        var errorTolerance = Math.Max(0, Math.Abs(net) - unknown) / total / 2;
        var pressure = new[] { .01m, .02m, .05m }.Select(fraction =>
        {
            var stressedLower = Math.Max(-1, lower - 2 * fraction);
            var stressedUpper = Math.Min(1, upper + 2 * fraction);
            return new PressureResult(fraction, stressedLower, stressedUpper,
                stressedLower > 0 || stressedUpper < 0);
        }).ToArray();

        return new(total, classified == 0 ? null : net / classified, (classified + mid) / total,
            lower, upper, errorTolerance, pressure,
            lower > 0 ? "BUY" : upper < 0 ? "SELL" : "UNCERTAIN",
            pressure.Where(x => x.Passed).Select(x => (decimal?)x.Fraction).Max());
    }
}
