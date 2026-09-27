using IbOptionFlowProbe;

namespace IbOptionFlowProbe.Tests;

internal static class DirectionMetricsTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("direction known 60/30/10 example", () =>
        {
            var actual = DirectionMetrics.Calculate(60, 30, 10);
            Check(actual.Total == 100 && actual.Tendency == 30m / 90m && actual.Coverage == .9m,
                "Classified tendency and total coverage must use separate denominators");
            Check(actual.Lower == .2m && actual.Upper == .4m && actual.ErrorTolerance == .1m,
                "Unknown mass must widen the range and reduce error tolerance");
            Check(actual.Direction == "BUY" && actual.HighestPassedPressure == .05m,
                "This example must survive all pressure levels");
            Check(actual.Pressure.Select(x => x.Fraction).SequenceEqual(new[] { .01m, .02m, .05m }),
                "Pressure levels must be 1%, 2%, 5%");
            Check(actual.Pressure[0] == new PressureResult(.01m, .18m, .42m, true)
                && actual.Pressure[1] == new PressureResult(.02m, .16m, .44m, true)
                && actual.Pressure[2] == new PressureResult(.05m, .10m, .50m, true),
                "Pressure must move each endpoint by twice the error fraction");
            return Task.CompletedTask;
        });
        await test("direction empty data has nullable metrics", () =>
        {
            var actual = DirectionMetrics.Calculate(0, 0, 0);
            Check(actual.Total == 0 && actual.Tendency == null && actual.Coverage == null
                && actual.Lower == null && actual.Upper == null && actual.ErrorTolerance == null
                && actual.HighestPassedPressure == null && actual.Pressure.Length == 0
                && actual.Direction == "NO_DATA", "Empty data must not imply neutral or certain flow");
            return Task.CompletedTask;
        });
        await test("direction all unknown data spans both directions", () =>
        {
            var actual = DirectionMetrics.Calculate(0, 0, 100);
            Check(actual.Tendency == null && actual.Coverage == 0 && actual.Lower == -1
                && actual.Upper == 1 && actual.ErrorTolerance == 0 && actual.Direction == "UNCERTAIN"
                && actual.HighestPassedPressure == null, "Unknown data cannot establish a direction");
            Check(actual.Pressure.All(x => x.Lower == -1 && x.Upper == 1 && !x.Passed),
                "All pressure ranges must remain clipped to [-1, 1]");
            return Task.CompletedTask;
        });
        await test("direction one-sided flow and sell symmetry", () =>
        {
            var buy = DirectionMetrics.Calculate(100, 0, 0);
            var sell = DirectionMetrics.Calculate(0, 100, 0);
            Check(buy.Tendency == 1 && buy.Lower == 1 && buy.Upper == 1 && buy.Direction == "BUY"
                && buy.Coverage == 1 && buy.ErrorTolerance == .5m, "All-buy flow must have half-total tolerance");
            Check(sell.Tendency == -1 && sell.Lower == -1 && sell.Upper == -1 && sell.Direction == "SELL"
                && sell.Coverage == 1 && sell.ErrorTolerance == .5m, "Sell metrics must mirror buy metrics");
            Check(buy.Pressure.All(x => x.Upper == 1 && x.Passed)
                && sell.Pressure.All(x => x.Lower == -1 && x.Passed), "One-sided pressure ranges must clip");
            for (var i = 0; i < buy.Pressure.Length; i++)
                Check(buy.Pressure[i].Lower == -sell.Pressure[i].Upper, "Pressure must be sign symmetric");
            return Task.CompletedTask;
        });
        await test("direction weak flow crosses zero", () =>
        {
            foreach (var actual in new[] { DirectionMetrics.Calculate(45, 35, 20), DirectionMetrics.Calculate(35, 45, 20) })
                Check(actual.Direction == "UNCERTAIN" && actual.Lower < 0 && actual.Upper > 0
                    && actual.ErrorTolerance == 0 && actual.HighestPassedPressure == null
                    && actual.Pressure.All(x => !x.Passed), "Unknown flow outweighs the observed imbalance");
            Check(DirectionMetrics.Calculate(50, 40, 10).Direction == "UNCERTAIN",
                "A base interval touching zero cannot establish direction");
            Check(DirectionMetrics.Calculate(40, 50, 10).Direction == "UNCERTAIN",
                "Sell boundary touching zero cannot establish direction");
            return Task.CompletedTask;
        });
        await test("direction strict 1% 2% 5% pressure boundaries", () =>
        {
            foreach (var fraction in new[] { .01m, .02m, .05m })
            {
                foreach (var sign in new[] { 1, -1 })
                {
                    var actual = DirectionMetrics.Calculate(50 + sign * fraction * 100, 50 - sign * fraction * 100, 0);
                    var boundary = actual.Pressure.Single(x => x.Fraction == fraction);
                    Check(actual.ErrorTolerance == fraction && !boundary.Passed
                        && (sign > 0 ? boundary.Lower == 0 : boundary.Upper == 0),
                        "Exactly zero at a pressure boundary must fail");
                    Check(actual.Pressure.All(x => x.Passed == (x.Fraction < fraction)),
                        "Only pressure strictly below tolerance can pass");
                    decimal? expectedHighest = fraction == .01m ? null : fraction == .02m ? .01m : .02m;
                    Check(actual.HighestPassedPressure == expectedHighest, "Highest passed pressure must exclude the boundary");
                }
            }
            return Task.CompletedTask;
        });
        await test("direction increasing unknown and pressure cannot improve certainty", () =>
        {
            DirectionMetrics? previous = null;
            foreach (var unknown in new[] { 0m, 5m, 10m, 20m, 30m, 100m })
            {
                var actual = DirectionMetrics.Calculate(60, 30, unknown);
                if (previous != null)
                    Check(actual.Coverage <= previous.Coverage && actual.Lower <= previous.Lower
                        && actual.Upper >= previous.Upper && actual.ErrorTolerance <= previous.ErrorTolerance,
                        "More unknown mass must weaken evidence and widen its range");
                for (var i = 1; i < actual.Pressure.Length; i++)
                    Check(actual.Pressure[i].Lower <= actual.Pressure[i - 1].Lower
                        && actual.Pressure[i].Upper >= actual.Pressure[i - 1].Upper
                        && (!actual.Pressure[i].Passed || actual.Pressure[i - 1].Passed),
                        "Higher pressure must widen the interval and cannot restore a failed direction");
                previous = actual;
            }
            return Task.CompletedTask;
        });
        await test("direction representable decimal extremes avoid intermediate overflow", () =>
        {
            foreach (var amount in new[] { decimal.MaxValue, .0000000000000000000000000001m })
            {
                var actual = DirectionMetrics.Calculate(amount, 0, 0);
                Check(actual.Total == amount && actual.ErrorTolerance == .5m && actual.Lower == 1
                    && actual.Pressure[2].Lower == .9m, "Scale must not overflow or underflow normalized results");
                var unknown = DirectionMetrics.Calculate(0, 0, amount);
                Check(unknown.Lower == -1 && unknown.Upper == 1 && unknown.ErrorTolerance == 0,
                    "All-unknown extreme must remain representable");
            }
            var unit = decimal.MaxValue / 10;
            var mixed = DirectionMetrics.Calculate(unit * 6, unit * 3, unit);
            Check(Math.Abs(mixed.Lower!.Value - .2m) < .00000000000000000000000001m
                && Math.Abs(mixed.ErrorTolerance!.Value - .1m) < .00000000000000000000000001m,
                "Large mixed values must retain normalized metrics");
            return Task.CompletedTask;
        });
        await test("direction rejects every negative input", () =>
        {
            foreach (var values in new[] { (-1m, 0m, 0m), (0m, -1m, 0m), (0m, 0m, -1m) })
            {
                var rejected = false;
                try { DirectionMetrics.Calculate(values.Item1, values.Item2, values.Item3); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "Negative evidence must be rejected");
            }
            return Task.CompletedTask;
        });
    }
}
