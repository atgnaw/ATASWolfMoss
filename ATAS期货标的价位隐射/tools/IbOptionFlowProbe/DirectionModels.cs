using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal sealed record DirectionScenario(string Id, string Method, int? OffsetMs, int MaximumAgeMs);
internal sealed record DirectionSettings
{
    public int HistorySeconds { get; init; } = 60;
    public bool ClassifyOutsideQuote { get; init; }
    public int HistoryCapacityPerContract { get; init; } = 65536;
    public int ClockJumpThresholdMs { get; init; } = 250;
    public int TickMaximumAgeMs { get; init; } = 5000;
    public int MinimumEvents { get; init; } = 30;
    public decimal[] ErrorPressureFractions { get; init; } = [.01m, .02m, .05m];
    public int[] SourceOffsetsMs { get; init; } = [-250, -100, 0, 100, 250];
    public DirectionScenario[] Scenarios { get; init; } = Build();
    private static DirectionScenario[] Build()
    {
        var methods = new[] { "AtQuote", "Midpoint", "MidpointTick" };
        return methods.SelectMany(m => QuoteAgeThresholds.Values.Select(age => new DirectionScenario($"Latest/{m}/{age}", m, null, age)))
            .Concat(new[] { -250, -100, 0, 100, 250 }.SelectMany(offset => methods.Select(m => new DirectionScenario($"Source{offset:+0;-0;0}/{m}", m, offset, -1)))).ToArray();
    }
}
internal sealed record AlignmentEvidence(string Id, DateTime TargetUtc, double TargetTicks, bool Feasible,
    QuoteState? Quote, string? Error, double? BidAgeMs, double? AskAgeMs);
internal sealed record DirectionMatch(string ScenarioId, string AlignmentId, int Sign, string Reason);
internal sealed record DirectionEvidence(long Sequence, string Disposition, int StableSign, string StableReason,
    int FeasibleSourceCandidates, int DistinctQuoteStates, int TickSign, AlignmentEvidence[] Alignments, DirectionMatch[] Matches)
{
    public DirectionMatch[] Consensus { get; init; } = [];
}

internal sealed class DirectionAmounts
{
    public SortedDictionary<string, UnknownReasonGroup> UnresolvedGroups { get; } = new(StringComparer.Ordinal);
    public decimal Buy { get; set; }
    public decimal Sell { get; set; }
    public decimal Unknown { get; set; }
    public decimal Mid { get; set; }
    public SortedDictionary<string, decimal> Reasons { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, decimal> UnknownPremiums { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> UnknownCounts { get; } = new(StringComparer.Ordinal);
    public DirectionMetrics Metrics => DirectionMetrics.Calculate(Buy, Sell, Unknown, Mid);
    public void AddUnresolved(decimal premium, string[] reasons, Func<UnknownTradeExample> example)
    {
        var distinct = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0) throw new ArgumentException("Unresolved trade requires a candidate reason.");
        var key = string.Join("|", distinct);
        if (!UnresolvedGroups.TryGetValue(key, out var group))
            UnresolvedGroups[key] = group = new(distinct);
        group.Premium += premium; group.Events++;
        if (group.Examples.Count < 2) group.Examples.Add(example());
    }
    public void Add(int sign, decimal premium, string reason)
    {
        var mid = sign == 0 && DirectionClassifier.IsMid(reason);
        if (sign > 0) Buy += premium; else if (sign < 0) Sell += premium; else if (mid) Mid += premium; else Unknown += premium;
        Reasons[reason] = Reasons.GetValueOrDefault(reason) + premium;
        if (sign == 0 && !mid)
        {
            UnknownPremiums[reason] = UnknownPremiums.GetValueOrDefault(reason) + premium;
            UnknownCounts[reason] = UnknownCounts.GetValueOrDefault(reason) + 1;
        }
    }
}
internal sealed class UnknownReasonGroup(string[] reasons)
{
    public string[] Reasons { get; } = reasons;
    public decimal Premium { get; set; }
    public long Events { get; set; }
    public List<UnknownTradeExample> Examples { get; } = new();
}
internal sealed record UnknownCandidate(string Alignment, DateTime TargetUtc, decimal? Bid, decimal? Ask,
    long? BidSequence, long? AskSequence, int Sign, string Reason);
internal sealed record UnknownTradeExample(long Sequence, DateTime SourceUtc, DateTime ReceivedUtc,
    decimal Price, decimal Premium, UnknownCandidate[] Candidates);
internal sealed class DirectionBucket
{
    public required OptionContractDescriptor Contract { get; init; }
    public required string Scope { get; init; }
    public required string Session { get; init; }
    public int IntervalMinutes { get; init; }
    public DateTime? StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
    public long Events { get; set; }
    public decimal Volume { get; set; }
    public DirectionAmounts Primary { get; } = new();
    public SortedDictionary<string, DirectionAmounts> Scenarios { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, DirectionAmounts> Consensus { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, decimal> TimeConflictPremiums { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Quality { get; } = new(StringComparer.Ordinal);
    public decimal[] LargestPremiums { get; } = new decimal[5];
    public decimal? LargestFraction => QuoteScenario.Divide(LargestPremiums[0], Primary.Metrics.Total);
    public decimal? TopFiveFraction => QuoteScenario.Divide(LargestPremiums.Sum(), Primary.Metrics.Total);
    public decimal? ReconciliationVolumeCoverage { get; set; }
    public decimal? UnexplainedVolume { get; set; }
    public decimal? PremiumDifference { get; set; }
    public bool Assessable => Events >= 30 && !Quality.Any(x => x is "LOCAL_INCOMPLETE" or "PARTIAL_BUCKET" or "NON_REALTIME" or "OBSERVATION_GAP" or "CLOCK_JUMP");
    public string Status => !Assessable ? "INSUFFICIENT_EVIDENCE" : Primary.Metrics.Direction;
    public string Extent => Quality.Count == 0 ? "OBSERVED_TRADES_ONLY" : "OBSERVED_SUBSET_ONLY";
    public decimal? HighestPassedPressure => Assessable ? Primary.Metrics.HighestPassedPressure : null;
    public void TrackLargest(decimal value)
    {
        for (var i = 0; i < LargestPremiums.Length; i++) if (value > LargestPremiums[i])
        { for (var j = LargestPremiums.Length - 1; j > i; j--) LargestPremiums[j] = LargestPremiums[j - 1]; LargestPremiums[i] = value; break; }
    }
}
internal sealed record DirectionReport(DirectionSettings Settings, SortedDictionary<string, long> Diagnostics,
    DirectionBucket[] Totals, DirectionBucket[] Buckets, int PeakHistoryStatesPerContract, long CapacityEvictions)
{
    public UnknownReason[] ReasonCatalog => UnknownReasons.All;
}
