namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private static readonly TimeSpan OptionFlowPreparationLead = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OptionFlowCompletionLag = TimeSpan.FromMinutes(1);
    private IbOptionSubscriptionRequirements _activeOptionRequirements;

    private async Task MaintainOptionSubscriptionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OptionContractDescriptor> contracts;
        string? ticker;

        lock (_optionDataSync)
        {
            EnsureSharedFlow(RealtimeUtcNow());
            contracts = _activeOptionContracts.ToArray();
            ticker = _activeOptionTicker;
        }

        var shouldSubscribe = _showOptionPremiumFlow
                              && contracts.Count > 0
                              && ticker != null
                              && IsInsideOptionFlowSubscriptionWindow(
                                  RealtimeUtcNow(), ticker, contracts);

        if (!shouldSubscribe)
        {
            lock (_optionDataSync) _sharedFlow?.Observe(false, RealtimeUtcNow());
            _optionSubscription?.Dispose();
            _optionSubscription = null;
            return;
        }

        var requirements = new IbOptionSubscriptionRequirements(
            true, // Daily OI on existing Flow lines; never re-subscribe just to toggle the OI column.
            _optionFlowTradeScope == OptionFlowTradeScope.RegularTrades,
            _optionFlowTradeScope == OptionFlowTradeScope.AllTimeAndSales);
        if (_optionSubscription != null)
        {
            var ids = contracts.Select(static c => c.ConId).Order().ToArray();
            if (!_optionSubscription.ContractIds.Order().SequenceEqual(ids) || _activeOptionRequirements != requirements)
            {
                await _optionSubscription.UpdateAsync(contracts, requirements,
                    _ibOptionMarketDataLineBudget, cancellationToken).ConfigureAwait(false);
                _activeOptionRequirements = requirements;
            }
            lock (_optionDataSync) _sharedFlow?.Observe(true, RealtimeUtcNow());
            if (_optionGatewayLease?.Client is ReflectionIbOptionGatewayClient connected)
                await connected.RepairSubscriptionsAsync(RealtimeUtcNow(), _ibOptionMarketDataLineBudget, cancellationToken).ConfigureAwait(false);
            return;
        }
        var gateway = GetOrCreateOptionGateway();
        lock (_optionDataSync)
        {
            _sharedFlow?.Observe(true, RealtimeUtcNow());
            _rollingFlow.Resume(RealtimeUtcNow());
            _flowCoverageStartUtc = RealtimeUtcNow();
        }
        var subscription = await gateway.SubscribeAsync(
                contracts,
                requirements,
                update =>
                {
                    OnScheduledOptionMarketData(update, cancellationToken);
                },
                _ibOptionMarketDataLineBudget,
                cancellationToken)
            .ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            subscription.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_flowCoverageStartUtc == DateTime.MinValue)
            _flowCoverageStartUtc = RealtimeUtcNow();

        _optionSubscription = subscription;
        _activeOptionRequirements = requirements;
    }

    private static bool IsInsideOptionFlowSubscriptionWindow(
        DateTime nowUtc,
        string ticker,
        IReadOnlyList<OptionContractDescriptor> contracts)
    {
        var tradingHours = contracts
            .Select(static contract => contract.TradingHours)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var includeGlobalTradingHours = string.Equals(
            ticker, "SPX", StringComparison.OrdinalIgnoreCase);
        var timeZoneId = contracts
            .Select(static contract => contract.TradingTimeZoneId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        var segments = IbTradingHoursParser.Parse(
            tradingHours,
            timeZoneId,
            includeGlobalTradingHours);

        return segments.Any(segment =>
            nowUtc >= segment.StartUtc - OptionFlowPreparationLead
            && nowUtc <= segment.EndUtc + OptionFlowCompletionLag);
    }
}
