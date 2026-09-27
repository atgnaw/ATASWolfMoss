using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal static class QuoteAgeThresholds
{
    // -1 is an explicit unlimited-age scenario, not an arbitrarily large timeout.
    public static readonly int[] Values = [100, 250, 500, 1000, 2000, 5000, -1];
    public static string Label(int value) => value == -1 ? "不设年龄上限（沿用最后有效报价）" : $"{value} ms";
}

internal sealed class FlowAmounts
{
    public long Events { get; set; }
    public decimal Volume { get; set; }
    public decimal Premium { get; set; }
    public void Add(decimal volume, decimal premium) { Events++; Volume += volume; Premium += premium; }
}
internal sealed class QuoteScenario
{
    public int MaximumAgeMs { get; init; }
    public SortedDictionary<string, FlowAmounts> Categories { get; } = new(StringComparer.Ordinal)
    {
        ["AT_ASK"] = new(), ["AT_BID"] = new(), ["INSIDE_SPREAD"] = new(),
        ["OUTSIDE_QUOTE"] = new(), ["UNKNOWN"] = new()
    };
    public SortedDictionary<string, long> Reasons { get; } = new(StringComparer.Ordinal);
    public decimal? Tendency => Divide(Categories["AT_ASK"].Premium - Categories["AT_BID"].Premium,
        Categories["AT_ASK"].Premium + Categories["AT_BID"].Premium);
    public decimal? ClassifiedVolumeFraction => Divide(Categories["AT_ASK"].Volume + Categories["AT_BID"].Volume,
        Categories.Values.Sum(x => x.Volume));
    public decimal? ClassifiedPremiumFraction => Divide(Categories["AT_ASK"].Premium + Categories["AT_BID"].Premium,
        Categories.Values.Sum(x => x.Premium));
    internal static decimal? Divide(decimal a, decimal b) => b > 0 ? a / b : null;
}
internal sealed class AnalysisRow
{
    public required OptionContractDescriptor Contract { get; init; }
    public required string Scope { get; init; }
    public required string Session { get; init; }
    public int IntervalMinutes { get; init; } // 0 = session total
    public DateTime? BucketStartUtc { get; init; }
    public DateTime? BucketEndUtc { get; init; }
    public DateTime? ObservedFromUtc { get; set; }
    public DateTime? ObservedToUtc { get; set; }
    public bool Partial { get; set; }
    public long Events { get; set; }
    public decimal ObservedVolume { get; set; }
    public decimal ObservedPremium { get; set; }
    public decimal ComparableObservedVolume { get; set; }
    public decimal ComparableObservedPremium { get; set; }
    public decimal CumulativeVolumeChange { get; set; }
    public decimal CumulativePremiumChange { get; set; }
    public long ComparisonPairs { get; set; }
    public long BoundaryCrossings { get; set; }
    public long SensitivityChanges { get; set; }
    public long FutureSourceEvents { get; set; }
    public double MaximumSourceLeadMs { get; set; }
    public long DelayedEvents { get; set; }
    public long NearQuoteChangeEvents { get; set; }
    public SortedDictionary<string, long> Reasons { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> QuoteAgeHistogram { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> SourceLagHistogram { get; } = new(StringComparer.Ordinal);
    public QuoteScenario[] Scenarios { get; } = QuoteAgeThresholds.Values.Select(x => new QuoteScenario { MaximumAgeMs = x }).ToArray();
    public decimal? VolumeCoverage => ComparisonPairs > 0 ? QuoteScenario.Divide(ComparableObservedVolume, CumulativeVolumeChange) : null;
    public decimal? PremiumCoverage => ComparisonPairs > 0 ? QuoteScenario.Divide(ComparableObservedPremium, CumulativePremiumChange) : null;
    public decimal? UnexplainedVolume => ComparisonPairs > 0 ? CumulativeVolumeChange - ComparableObservedVolume : null;
    public decimal? PremiumDifference => ComparisonPairs > 0 ? CumulativePremiumChange - ComparableObservedPremium : null;
    public bool OverReconciled => ComparableObservedVolume > CumulativeVolumeChange && ComparisonPairs > 0;
    public string Evidence => OverReconciled || DelayedEvents > 0 ? "UNSUITABLE_OR_REVIEW"
        : Events < 30 || ComparisonPairs == 0 ? "INSUFFICIENT_SAMPLE"
        : "DESCRIPTIVE_ONLY";
    public void Count(string reason) => Reasons[reason] = Reasons.GetValueOrDefault(reason) + 1;
}
internal sealed record QuoteReference(long Sequence, decimal Price, long MonotonicTicks, DateTime ReceivedUtc,
    bool Unusable = false);
internal sealed record MatchDecision(int MaximumAgeMs, string Category, string Reason);
internal sealed record TradeDecision(Guid RunId, int ConnectionEpoch, long Sequence, int RequestId, long? ConId,
    string Scope, DateTime? SourceUtc, DateTime ReceivedUtc, string Disposition,
    long? BidSequence, long? AskSequence, long? BidSizeSequence, long? AskSizeSequence,
    double? BidAgeMs, double? AskAgeMs, double? SourceLagMs, MatchDecision[] Matches)
{
    public int SchemaVersion { get; init; } = 3;
    public DirectionEvidence? DirectionEvidence { get; init; }
    public DirectionEvidence? OutsideQuoteEvidence { get; init; }
}
internal sealed record AnalysisReport(int SchemaVersion, string AlgorithmVersion, Guid RunId,
    bool Synthetic, Inspection Integrity, string Conclusion, string[] Warnings,
    long RawEvents, long TradeCallbacks, long AcceptedTradeEvents, long Boundaries,
    SortedDictionary<string, long> Diagnostics, AnalysisRow[] Totals, AnalysisRow[] Buckets)
{
    public DirectionReport? Robustness { get; init; }
    public DirectionReport? OutsideQuoteRobustness { get; init; }
}
