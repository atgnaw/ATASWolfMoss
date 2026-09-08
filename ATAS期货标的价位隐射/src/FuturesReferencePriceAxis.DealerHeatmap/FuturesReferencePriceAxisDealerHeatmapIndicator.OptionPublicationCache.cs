namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    // Accessed by the data owner only; never stores mutable input dictionaries in snapshots.
    private IReadOnlyList<OptionTradingSegment>? _cachedFlowSegments;
    private string? _cachedFlowHours, _cachedFlowZone, _cachedFlowTicker;
    private IReadOnlyList<OptionContractDescriptor>? _publishedOiContracts;
    private IReadOnlyList<decimal>? _publishedOiStrikes;
    private OptionOpenInterestSnapshot? _publishedOi;
    private long _oiRevision, _publishedOiRevision;
    private int _publishedOptionLineCount;

    private IReadOnlyList<OptionTradingSegment> GetCachedFlowSegments(string ticker,
        IReadOnlyList<OptionContractDescriptor> contracts)
    {
        var hours = contracts.Count == 0 ? null : contracts[0].TradingHours;
        var zone = contracts.Count == 0 ? null : contracts[0].TradingTimeZoneId;
        if (_cachedFlowSegments != null && hours == _cachedFlowHours
            && zone == _cachedFlowZone && ticker == _cachedFlowTicker) return _cachedFlowSegments;
        _cachedFlowHours = hours; _cachedFlowZone = zone; _cachedFlowTicker = ticker;
        return _cachedFlowSegments = contracts.Count == 0 ? Array.Empty<OptionTradingSegment>()
            : IbTradingHoursParser.Parse(hours, zone,
                string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private OptionOpenInterestSnapshot GetPublishedOiSnapshot(string ticker, DateOnly expiration, decimal atm,
        IReadOnlyList<decimal> strikes, IReadOnlyList<OptionContractDescriptor> contracts, DateTime received)
    {
        if (_publishedOi is { } cached && cached.Ticker == ticker && cached.Expiration == expiration
            && cached.AtmStrikeUsd == atm && cached.ReceivedUtc == received
            && cached.RequestedStrikeCount == _optionStrikeLevels && _publishedOiRevision == _oiRevision
            && ReferenceEquals(_publishedOiContracts, contracts) && ReferenceEquals(_publishedOiStrikes, strikes))
            return cached;
        var rows = CreateRows(strikes, contracts, _optionOpenInterest, null);
        var receipts = contracts.ToDictionary(static contract => (contract.StrikeUsd, contract.Right),
            contract => _optionOiReceivedByContract.TryGetValue(contract.ConId, out var time) ? (DateTime?)time : null);
        var populated = contracts.Count(contract => _optionOpenInterest.ContainsKey(contract.ConId));
        var stale = contracts.Count(contract => _optionOpenInterest.ContainsKey(contract.ConId)
            && NeedsOiConfirmation(contract.ConId));
        var coverage = OptionContractCoverage.Calculate(strikes, contracts);
        var status = populated == 0 ? OptionDataStatus.WaitingOpenInterest
            : stale == populated ? OptionDataStatus.Frozen
            : stale > 0 ? OptionDataStatus.Partial
            : populated == contracts.Count && coverage.IsComplete ? OptionDataStatus.Daily : OptionDataStatus.Partial;
        _publishedOiContracts = contracts; _publishedOiStrikes = strikes; _publishedOiRevision = _oiRevision;
        return _publishedOi = new OptionOpenInterestSnapshot(ticker, expiration, atm, rows, status,
            populated > 0 ? received : DateTime.MinValue,
            $"OI {populated}/{contracts.Count}；合约 {coverage.ResolvedContractCount}/{coverage.ExpectedContractCount}"
                + (stale > 0 ? $"；旧值待确认 {stale}" : "；IB 接收值（非清算发布日期）")
                + (_oiCacheWriteFailed ? "；本地缓存写入失败，30 秒后重试" : string.Empty),
            _optionStrikeLevels, coverage.ActiveStrikeCount) { ReceivedByStrike = receipts };
    }
}
