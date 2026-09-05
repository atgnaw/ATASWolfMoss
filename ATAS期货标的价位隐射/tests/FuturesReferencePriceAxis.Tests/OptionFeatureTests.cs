using WolfMoss.ATAS.PriceMapping.Core;

internal static class OptionFeatureTests
{
    public static void Run()
    {
        Equal(5, OptionStrikeSelection.NormalizeLevelCount(4));
        Equal(21, OptionStrikeSelection.NormalizeLevelCount(20));
        Equal(21, OptionStrikeSelection.NormalizeLevelCount(99));

        var strikes = Enumerable.Range(90, 21)
            .Select(static value => (decimal)value)
            .ToArray();
        var selected = OptionStrikeSelection.SelectCentered(
            strikes, 100.5m, 21, out var atm);
        Equal(100m, atm);
        Equal(21, selected.Count);
        Equal(100m, OptionStrikeSelection.SelectCentered(strikes, 100.5m, 20, out _)[10]);
        var budgeted = OptionStrikeSelection.ApplySymmetricBudget(
            selected, atm, 10);
        Equal(5, budgeted.Count);
        Equal(new[] { 98m, 99m, 100m, 101m, 102m }, budgeted.ToArray());
        Equal((99.5m, 100.5m),
            OptionStrikeSelection.GetRowBounds(strikes, 10));
        Equal((97.5m, 105m),
            OptionStrikeSelection.GetRowBounds(new[] { 95m, 100m, 110m }, 1));
        Equal(0, OptionStrikeSelection.ApplySymmetricBudget(selected, atm, 1).Count);

        var probeCandidates = OptionStrikeSelection.SelectCenteredCandidates(
            Enumerable.Range(80, 41).Select(static value => (decimal)value),
            100m,
            37,
            out var probeAtm);
        Equal(100m, probeAtm);
        Equal(37, probeCandidates.Count);
        var verifiedStrikes = OptionStrikeSelection.SelectCenteredCandidates(
            new[] { 720m, 721m, 722m, 723m, 724m },
            722m,
            5,
            out _);
        Equal(new[] { 720m, 721m, 722m, 723m, 724m }, verifiedStrikes.ToArray());
        Equal((721.5m, 722.5m),
            OptionStrikeSelection.GetRowBounds(verifiedStrikes, 2));
        Equal((722.5m, 723.5m),
            OptionStrikeSelection.GetRowBounds(verifiedStrikes, 3));

        // OI and Premium/Volume project rows from the same verified ladder.
        // An invalid security-definition candidate such as QQQ 722.5 must not
        // reappear in either column or split the 722/723 row boundary.
        var flowRows = verifiedStrikes
            .Select(static strike => new OptionStrikeRow(
                strike,
                null,
                null,
                strike * 100m,
                strike * 50m,
                1,
                1))
            .ToArray();
        Assert(!flowRows.Any(static row => row.StrikeUsd == 722.5m));
        var flowStrikes = flowRows
            .Select(static row => row.StrikeUsd)
            .OrderBy(static strike => strike)
            .ToArray();
        Equal((721.5m, 722.5m),
            OptionStrikeSelection.GetRowBounds(flowStrikes, 2));
        Equal((722.5m, 723.5m),
            OptionStrikeSelection.GetRowBounds(flowStrikes, 3));
        Equal(72_400m, OptionPresentation.GetPremiumMaximum(flowRows));

        var expiration = new DateOnly(2026, 8, 31);
        var partialContracts = new[]
        {
            Contract(1, 100m, OptionRight.Call, expiration),
            Contract(2, 100m, OptionRight.Put, expiration),
            Contract(3, 101m, OptionRight.Call, expiration)
        };
        var partialCoverage = OptionContractCoverage.Calculate(
            new[] { 100m, 101m, 102m },
            partialContracts);
        Equal(6, partialCoverage.ExpectedContractCount);
        Equal(3, partialCoverage.ResolvedContractCount);
        Equal(2, partialCoverage.ActiveStrikeCount);
        Assert(!partialCoverage.IsComplete);
        Assert(OptionContractCoverage.Calculate(
            new[] { 100m },
            partialContracts[..2]).IsComplete);

        var lineRequests = new[]
        {
            Contract(1, 100m, OptionRight.Call, expiration),
            Contract(2, 100m, OptionRight.Put, expiration),
            Contract(3, 101m, OptionRight.Call, expiration),
            Contract(3, 101m, OptionRight.Call, expiration)
        };
        var constrainedLines = OptionMarketDataLineAllocator.Allocate(
            lineRequests,
            new long[] { 1 },
            2);
        Equal(3, constrainedLines.RequestedContractCount);
        Equal(2, constrainedLines.RequiredNewLineCount);
        Equal(1, constrainedLines.AllocatedNewLineCount);
        Equal(new long[] { 1, 2 },
            constrainedLines.Contracts.Select(static contract => contract.ConId).ToArray());
        Assert(!constrainedLines.IsComplete);

        var completeLines = OptionMarketDataLineAllocator.Allocate(
            lineRequests,
            new long[] { 1 },
            3);
        Assert(completeLines.IsComplete);
        Equal(new long[] { 1, 2, 3 },
            completeLines.Contracts.Select(static contract => contract.ConId).ToArray());
        var sharedWithoutCapacity = OptionMarketDataLineAllocator.Allocate(
            lineRequests,
            new long[] { 1, 99 },
            1);
        Equal(new long[] { 1 },
            sharedWithoutCapacity.Contracts.Select(static contract => contract.ConId).ToArray());

        var start = new OptionCumulativeSample(
            123, Utc(2026, 8, 27, 13, 30), 100, 2m, 100m);
        var end = new OptionCumulativeSample(
            123, Utc(2026, 8, 27, 13, 35), 110, 2.1m, 100m);
        Assert(OptionFlowAggregation.TryCalculateDelta(start, end, out var delta));
        Equal(10L, delta.Volume);
        Equal(3_100m, delta.Premium);
        Equal(start.SampleUtc, delta.ObservedStartUtc);
        Assert(!delta.IsPartial);
        Assert(!OptionFlowAggregation.TryCalculateDelta(end, start, out _));
        Assert(!OptionFlowAggregation.TryCalculateDelta(
            start,
            end with { TotalVolume = 90 },
            out _));
        Assert(!OptionFlowAggregation.TryCalculateDelta(
            start,
            end with { Multiplier = 50m },
            out _));
        Equal(1, OptionFlowAggregation.NormalizeInterval(1));
        Equal(3, OptionFlowAggregation.NormalizeInterval(3));
        Equal(5, OptionFlowAggregation.NormalizeInterval(4));
        Equal(10, OptionFlowAggregation.NormalizeInterval(10));

        var bucketStart = Utc(2026, 8, 27, 13, 30);
        var bucketEnd = Utc(2026, 8, 27, 13, 35);
        var completeSamples = new[]
        {
            new OptionCumulativeSample(
                123, Utc(2026, 8, 27, 13, 29, 59), 100, 2m, 100m),
            new OptionCumulativeSample(
                123, Utc(2026, 8, 27, 13, 34), 110, 2.1m, 100m, 2.1m, 1)
        };
        Assert(OptionFlowAggregation.TryCalculateBucketValue(
            completeSamples, bucketStart, bucketEnd, true, out var completeBucket));
        Equal(10L, completeBucket.Volume);
        Equal(3_100m, completeBucket.Premium);
        Equal(bucketStart, completeBucket.ObservedStartUtc);
        Assert(!completeBucket.IsPartial);

        var partialSamples = new[]
        {
            new OptionCumulativeSample(
                456, Utc(2026, 8, 27, 13, 33), 105, 2m, 100m, 3m, 2),
            new OptionCumulativeSample(
                456, Utc(2026, 8, 27, 13, 34), 110, 2.1m, 100m, 2.1m, 1)
        };
        Assert(partialSamples[0].TryGetLastTradeValue(out var firstObservedTrade));
        Equal(2L, firstObservedTrade.Volume);
        Equal(600m, firstObservedTrade.Premium);
        Assert(firstObservedTrade.IsPartial);
        Assert(OptionFlowAggregation.TryCalculateBucketValue(
            partialSamples, bucketStart, bucketEnd, true, out var partialBucket));
        Equal(7L, partialBucket.Volume);
        Equal(2_700m, partialBucket.Premium);
        Equal(partialSamples[0].SampleUtc, partialBucket.ObservedStartUtc);
        Assert(partialBucket.IsPartial);
        Assert(!OptionFlowAggregation.TryCalculateBucketValue(
            partialSamples, bucketStart, bucketEnd, false, out _));
        Assert(!OptionFlowAggregation.TryCalculateBucketValue(
            completeSamples[..1], bucketStart, bucketEnd, true, out _));

        var grace = TimeSpan.FromSeconds(3);
        Assert(OptionFlowAggregation.ShouldHoldFixedLadder(
            bucketStart,
            bucketEnd,
            bucketEnd.AddSeconds(2),
            grace));
        Assert(!OptionFlowAggregation.ShouldHoldFixedLadder(
            bucketStart,
            bucketEnd,
            bucketEnd.AddSeconds(3),
            grace));
        Assert(OptionFlowAggregation.ShouldHoldFixedLadder(
            bucketStart,
            bucketStart,
            bucketEnd,
            grace));

        var segment = new OptionTradingSegment(
            Utc(2026, 8, 27, 13, 30),
            Utc(2026, 8, 27, 20, 0));
        var completed = OptionFlowAggregation.GetPreviousCompletedBucket(
            Utc(2026, 8, 27, 13, 37),
            segment,
            5,
            TimeSpan.FromSeconds(3));
        Assert(completed.HasValue);
        var completedValue = completed.GetValueOrDefault();
        Equal(Utc(2026, 8, 27, 13, 30), completedValue.StartUtc);
        Equal(Utc(2026, 8, 27, 13, 35), completedValue.EndUtc);
        Equal<(DateTime, DateTime)?>(null,
            OptionFlowAggregation.GetPreviousCompletedBucket(
                Utc(2026, 8, 27, 13, 35, 2),
                segment,
                5,
                TimeSpan.FromSeconds(3)));

        var oneMinuteCompleted = OptionFlowAggregation.GetPreviousCompletedBucket(
            Utc(2026, 8, 27, 13, 31, 3),
            segment,
            1,
            TimeSpan.FromSeconds(3));
        Assert(oneMinuteCompleted.HasValue);
        Equal(Utc(2026, 8, 27, 13, 30),
            oneMinuteCompleted.GetValueOrDefault().StartUtc);
        Equal(Utc(2026, 8, 27, 13, 31),
            oneMinuteCompleted.GetValueOrDefault().EndUtc);
        Equal<(DateTime, DateTime)?>(null,
            OptionFlowAggregation.GetPreviousCompletedBucket(
                Utc(2026, 8, 27, 13, 31, 2),
                segment,
                1,
                TimeSpan.FromSeconds(3)));

        var rolling = new OptionRollingWindow();
        rolling.Add(start, TimeSpan.FromMinutes(5));
        rolling.Add(end with { SampleUtc = Utc(2026, 8, 27, 13, 34) },
            TimeSpan.FromMinutes(5));
        Assert(!rolling.TryGetValue(
            123, Utc(2026, 8, 27, 13, 34), TimeSpan.FromMinutes(5), out _));
        rolling.Add(end, TimeSpan.FromMinutes(5));
        Assert(rolling.TryGetValue(
            123, Utc(2026, 8, 27, 13, 35), TimeSpan.FromMinutes(5), out var rollingValue));
        Equal(delta, rollingValue);
        rolling.Add(new OptionCumulativeSample(
                123, Utc(2026, 8, 27, 13, 36), 1, 2m, 100m),
            TimeSpan.FromMinutes(5));
        Assert(!rolling.TryGetValue(
            123, Utc(2026, 8, 27, 13, 36), TimeSpan.FromMinutes(5), out _));

        var oneMinuteRolling = new OptionRollingWindow();
        oneMinuteRolling.Add(start, TimeSpan.FromMinutes(1));
        oneMinuteRolling.Add(
            end with { SampleUtc = Utc(2026, 8, 27, 13, 31) },
            TimeSpan.FromMinutes(1));
        Assert(oneMinuteRolling.TryGetValue(
            123,
            Utc(2026, 8, 27, 13, 31),
            TimeSpan.FromMinutes(1),
            out var oneMinuteRollingValue));
        Equal(delta, oneMinuteRollingValue);

        var rows = new[]
        {
            new OptionStrikeRow(100m, 50, 100, 2_000m, 1_000m, 3, 4),
            new OptionStrikeRow(101m, 25, 10, 500m, 250m, 1, 1)
        };
        Equal(100m, OptionPresentation.GetOpenInterestMaximum(rows));
        Equal(2_000m, OptionPresentation.GetPremiumMaximum(rows));
        Equal(0.5m, OptionPresentation.GetFillRatio(50m, 100m));
        Equal("1.25M", OptionPresentation.FormatCompact(1_250_000m));
        Assert(!OptionPresentation.FormatCompact(1_250_000m).Contains('$'));
        Equal(0m, OptionPresentation.GetFillRatio(null, 100m));
        Equal(0m, OptionPresentation.GetFillRatio(0m, 100m));
        Equal("·", OptionPresentation.GetFlowMissingMarker(OptionDataStatus.Live));
        Equal("?", OptionPresentation.GetFlowMissingMarker(OptionDataStatus.Frozen));
        Equal("NO EVENTS", OptionPresentation.GetFlowCoverageLabel(
            OptionDataStatus.Live, hasValue: false, isPartial: false));
        Equal("NO DATA", OptionPresentation.GetFlowCoverageLabel(
            OptionDataStatus.NoPermission, hasValue: false, isPartial: false));
        Equal("PARTIAL", OptionPresentation.GetFlowCoverageLabel(
            OptionDataStatus.Live, hasValue: true, isPartial: true));

        var allColumns = DealerColumnPlanner.FromToggles(true, true, true, true);
        var plan = DealerColumnPlanner.Create(72, 72, allColumns);
        Equal(4, plan.ColumnCount);
        Assert(plan.TryGetLeft(DealerColumnKind.Heatmap, out var heatmap));
        Assert(plan.TryGetLeft(DealerColumnKind.DealerGex, out var gex));
        Assert(plan.TryGetLeft(DealerColumnKind.OptionOpenInterest, out var oi));
        Assert(plan.TryGetLeft(DealerColumnKind.OptionPremiumFlow, out var flow));
        Equal(72, heatmap);
        Equal(144, gex);
        Equal(216, oi);
        Equal(288, flow);
        Equal(42, DealerColumnPlanner.CalculateColumnWidth(72, 300, allColumns));

        Assert(IbErrorCallbackParser.TryParse(
            new object?[] { 42, 354, "No permission" }, out var legacyError));
        Equal(42, legacyError.RequestId);
        Equal(354, legacyError.ErrorCode);
        Equal("No permission", legacyError.Message);
        Assert(IbErrorCallbackParser.TryParse(
            new object?[]
            {
                43,
                1_787_830_400L,
                101,
                "Ticker limit",
                string.Empty
            }, out var modernError));
        Equal(43, modernError.RequestId);
        Equal(101, modernError.ErrorCode);
        Equal("Ticker limit", modernError.Message);
        Assert(!IbErrorCallbackParser.TryParse(new object?[] { 1, "bad" }, out _));

        var tradingHours =
            "20260827:0930-20260827:1600;20260827:2000-20260828:1700";
        Equal(1, IbTradingHoursParser.ParseEastern(tradingHours, false).Count);
        Equal(2, IbTradingHoursParser.ParseEastern(tradingHours, true).Count);
        var central = IbTradingHoursParser.Parse(
            "20260827:0830-20260827:1500",
            "US/Central",
            includeGlobalTradingHours: true).Single();
        Equal(Utc(2026, 8, 27, 13, 30), central.StartUtc);
        Equal(Utc(2026, 8, 27, 20, 0), central.EndUtc);
        TestOpenInterestCache();
    }

    private static void TestOpenInterestCache()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WolfMoss-OptionOiTests-" + Guid.NewGuid().ToString("N"));

        try
        {
            var expiration = new DateOnly(2026, 8, 27);
            var receivedUtc = Utc(2026, 8, 27, 12, 5);
            OptionOpenInterestCache.Save(
                directory,
                "QQQ",
                expiration,
                receivedUtc,
                new Dictionary<long, long> { [101] = 0, [102] = 2_500 });
            var loaded = OptionOpenInterestCache.LoadSnapshot(
                directory, "qqq", expiration);
            Equal(receivedUtc, loaded.ReceivedUtc);
            Equal(2, loaded.Values.Count);
            Equal(0L, loaded.Values[101]);
            Equal(2_500L, loaded.Values[102]);
            Assert(!loaded.Values.ContainsKey(999));
            Equal(0, OptionOpenInterestCache.LoadSnapshot(
                directory, "QQQ", expiration.AddDays(1)).Values.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static DateTime Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private static OptionContractDescriptor Contract(
        long conId,
        decimal strike,
        OptionRight right,
        DateOnly expiration)
        => new(
            conId,
            "QQQ",
            expiration,
            strike,
            right,
            "QQQ",
            "SMART",
            100m,
            string.Empty);

    private static void Assert(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Option feature assertion failed.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private static void Equal<T>(T[] expected, T[] actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}]");
        }
    }
}
