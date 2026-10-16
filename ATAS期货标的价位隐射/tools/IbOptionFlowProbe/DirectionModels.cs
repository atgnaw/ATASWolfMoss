using WolfMoss.ATAS.PriceMapping.Core;

namespace IbOptionFlowProbe;

internal sealed record DirectionScenario(string Id, string Method, int? OffsetMs, int MaximumAgeMs, int? WaitMs = null, bool Conditional = false);
internal sealed record LargeTradeSettings(int QqqContracts = 100, int SpxContracts = 50)
{
    public static LargeTradeSettings Parse(string[] args)
    {
        var result = new LargeTradeSettings();
        var seen = new HashSet<string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]) || !int.TryParse(args[i + 1], out var value))
                throw new ArgumentException("大单阈值参数缺失、重复或不是整数。");
            result = args[i] switch
            {
                "--large-qqq" => result with { QqqContracts = value },
                "--large-spx" => result with { SpxContracts = value },
                _ => throw new ArgumentException("未知分析参数：" + args[i])
            };
        }
        result.Validate(); return result;
    }
    public int Threshold(string ticker) => ticker == "SPX" ? SpxContracts : QqqContracts;
    public void Validate()
    {
        if (QqqContracts is < 1 or > 1000000 || SpxContracts is < 1 or > 1000000)
            throw new ArgumentException("大单张数阈值必须是 1 至 1000000 的整数。");
    }
}
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
    public static readonly int[] WaitThresholds = [50, 100, 200, 400];
    public int[] WaitMilliseconds { get; init; } = WaitThresholds;
    public string WaitRule { get; init; } = "MIN_BID_MAX_ASK_FIRST_PRICE_CHANGE";
    public LargeTradeSettings LargeTrades { get; init; } = new();
    public string ConditionalRule { get; init; } = "SIZE_GATE_50MS_FIRST_INVALID_PRE_ONLY";
    public DirectionScenario[] Scenarios { get; init; } = Build();
    private static DirectionScenario[] Build()
    {
        var methods = new[] { "AtQuote", "Midpoint", "MidpointTick" };
        return methods.SelectMany(m => QuoteAgeThresholds.Values.Select(age => new DirectionScenario($"Latest/{m}/{age}", m, null, age)))
            .Concat(new[] { -250, -100, 0, 100, 250 }.SelectMany(offset => methods.Select(m => new DirectionScenario($"Source{offset:+0;-0;0}/{m}", m, offset, -1))))
            .Concat(WaitThresholds.SelectMany(wait => methods.Select(m => new DirectionScenario($"Envelope/{m}/{wait}", m, null, -1, wait))))
            .Concat(methods.Select(m => new DirectionScenario($"Conditional/{m}/50", m, null, -1, 50, true))).ToArray();
    }
}
internal sealed record AlignmentEvidence(string Id, DateTime TargetUtc, double TargetTicks, bool Feasible,
    QuoteState? Quote, string? Error, double? BidAgeMs, double? AskAgeMs);
internal sealed record DirectionMatch(string ScenarioId, string AlignmentId, int Sign, string Reason);
internal sealed record DirectionEvidence(long Sequence, string Disposition, int StableSign, string StableReason,
    int FeasibleSourceCandidates, int DistinctQuoteStates, int TickSign, AlignmentEvidence[] Alignments, DirectionMatch[] Matches)
{
    public DirectionMatch[] Consensus { get; init; } = [];
    // Only the new delayed experiment is completed later; original evidence never changes.
    public List<DirectionMatch> WaitMatches { get; } = [];
    public List<QuoteWaitDecision> WaitDecisions { get; } = [];
    public AlignmentEvidence? ConditionalNextQuote { get; set; }
    public AlignmentEvidence? ConditionalSimulatedQuote { get; set; }
    public AlignmentEvidence? ConditionalFirstRejectedQuote { get; set; }
    public int ConditionalSkippedQuotes { get; set; }
    public SortedDictionary<string, int> ConditionalSkippedReasons { get; } = new(StringComparer.Ordinal);
    public QuoteWaitDecision? ConditionalWaitDecision { get; set; }
    public AlignmentEvidence? NextQuote { get; set; }
    public AlignmentEvidence? SimulatedQuote { get; set; }
    public int LargeTradeThreshold { get; set; }
    public bool IsLargeTrade { get; set; }
    public List<ConditionalMatch> ConditionalMatches { get; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Ready => Disposition != "ACCEPTED" || WaitDecisions.Count == DirectionSettings.WaitThresholds.Length && ConditionalMatches.Count == 3;
}
internal sealed record QuoteWaitDecision(int WaitMs, string Status, DateTime DecidedUtc);
internal sealed record ConditionalMatch(string ScenarioId, string AlignmentId, int Sign, string Reason, string Branch, DateTime DecidedUtc);

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
    public SortedDictionary<string, decimal> WaitPremiums { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, long> WaitCounts { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, decimal> WaitVolumes { get; } = new(StringComparer.Ordinal);
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
