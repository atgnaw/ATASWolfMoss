namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private SharedOptionFlow.Lease? _sharedFlow;
    private long _sharedFlowRangeRevision;

    // Data-owner lock only. This is in the scheduled path, never OnRender.
    private void EnsureSharedFlow(DateTime now)
    {
        if (!_showOptionPremiumFlow) { ReleaseSharedFlow(); return; }
        if (_optionGatewayLease == null || _activeOptionTicker == null
            || !_activeOptionExpiration.HasValue || _activeOptionContracts.Count == 0) return;
        var key = new OptionFlowGroupKey(_optionGatewayLease.ConnectionKey, _activeOptionTicker,
            _activeOptionExpiration.Value, _optionFlowBucketMode, _optionFlowIntervalMinutes,
            _optionFlowTradeScope, _optionStrikeLevels);
        if (_sharedFlow?.Key != key)
        {
            ReleaseSharedFlow();
            _sharedFlow = SharedOptionFlow.Acquire(key, now);
        }
        if (_tickerPlan != null) _sharedFlow.Book.SetReceiverBoundary(_tickerPlan.Revision, _tickerPlan.NextSwitchUtc);
        // A stale chart must not reactivate contracts after the common fence moved.
        if (_tickerRangeVersion != _tickerAppliedRangeVersion) return;
        _sharedFlow.Book.Configure(_activeOptionContracts, _activeAtmStrike,
            GetCachedFlowSegments(_activeOptionTicker, _activeOptionContracts),
            _sharedFlowRangeRevision, now);
    }

    private void ReleaseSharedFlow()
    {
        if (_sharedFlow == null) return;
        lock (_optionDataSync)
        {
            _sharedFlow?.Dispose();
            _sharedFlow = null;
        }
    }
}
