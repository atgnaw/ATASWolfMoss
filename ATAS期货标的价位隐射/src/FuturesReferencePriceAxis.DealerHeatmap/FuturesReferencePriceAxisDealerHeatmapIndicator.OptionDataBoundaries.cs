namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly Dictionary<long, DateTime> _optionOiReceivedByContract = new();
    private DateTime _oiConfirmationStartUtc, _nextOiCacheSaveUtc;
    private bool _oiCacheWriteFailed;

    // Data-owner lock only. No file/network work here.
    private void RefreshOiConfirmationEpoch(DateTime nowUtc)
    {
        var start = OptionOiFreshnessPolicy.ConfirmationStartUtc(nowUtc);
        if (start == _oiConfirmationStartUtc) return;
        _oiConfirmationStartUtc = start;
        _oiRevision++;
        _optionOiFastRetryCount = 0;
        _nextOiRetryUtc = nowUtc;
    }

    private bool NeedsOiConfirmation(long conId)
        => !_optionOpenInterest.ContainsKey(conId)
           || !_optionOiReceivedByContract.TryGetValue(conId, out var received)
           || received < _oiConfirmationStartUtc;

    private bool AcceptFixedSample(OptionCumulativeSample sample, DateTime received)
        => ClassifyFixedSample(sample, received) == FlowSampleDisposition.Accepted;

    private FlowSampleDisposition ClassifyFixedSample(OptionCumulativeSample sample, DateTime received)
    {
        if (!sample.HasValidCumulativeValue()) return FlowSampleDisposition.Invalid;
        if (sample.SampleUtc > received) return FlowSampleDisposition.FutureSource;
        if (sample.SampleUtc < _flowCoverageStartUtc) return FlowSampleDisposition.BeforeCoverage;
        if (_activeOptionTicker == null) return FlowSampleDisposition.OutsideLadder;
        var active = false;
        for (var i = 0; i < _activeOptionContracts.Count; i++)
            if (_activeOptionContracts[i].ConId == sample.ConId) { active = true; break; }
        if (!active) return FlowSampleDisposition.OutsideLadder;
        var segments = GetCachedFlowSegments(_activeOptionTicker, _activeOptionContracts);
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Contains(sample.SampleUtc)) return FlowSampleDisposition.Accepted;
        return FlowSampleDisposition.OutsideSession;
    }

    private void ReceiveFlowSample(OptionCumulativeSample sample, DateTime received, OptionFlowTradeScope scope)
    {
        FlowSampleDisposition disposition;
        if (_sharedFlow != null)
        {
            if (scope != _sharedFlow.Key.Scope) return;
            disposition = _sharedFlow.Book.ReceiveDetailed(sample, received);
        }
        else if (_optionFlowBucketMode == OptionFlowBucketMode.Rolling)
        {
            disposition = _rollingFlow.AddDetailed(sample, scope, received);
        }
        else
        {
            disposition = ClassifyFixedSample(sample, received);
            var destination = scope == OptionFlowTradeScope.RegularTrades ? _regularTradeSamples : _allTradeSamples;
            if (disposition == FlowSampleDisposition.Accepted)
            {
                if (destination.TryGetValue(sample.ConId, out var samples) && samples.Count > 0
                    && sample.SampleUtc < samples[^1].SampleUtc) disposition = FlowSampleDisposition.OutOfOrder;
                else AddCumulativeSample(destination, sample);
            }
        }
        if (_performance.Enabled && scope == _optionFlowTradeScope)
            _flowReception.Record(sample.SampleUtc, received, disposition,
                Volatile.Read(ref _optionFlowSnapshot).BucketEndUtc);
    }

    private void SuspendOptionObservation(DateTime nowUtc)
    {
        lock (_optionDataSync)
        {
            _sharedFlow?.Observe(false, nowUtc);
            _rollingFlow.Suspend(nowUtc);
            // Keep the last published frame, but never subtract across an unobserved connection gap.
            _regularTradeSamples.Clear();
            _allTradeSamples.Clear();
            _flowCoverageStartUtc = nowUtc;
        }
    }

    private bool TransitionOptionTarget(string ticker, DateOnly expiration, DateTime nowUtc, string? cacheDirectory = null)
    {
        if (_activeOptionTicker == ticker && _activeOptionExpiration == expiration) return false;
        Volatile.Write(ref _flowCountdownContext, null);
        ReleaseSharedFlow();
        SaveOpenInterestCacheIfNeeded();
        Interlocked.Exchange(ref _optionSubscription, null)?.Dispose();
        lock (_optionDataSync)
        {
            _activeOptionTicker = ticker;
            _activeOptionExpiration = expiration;
            _activeOptionContracts = Array.Empty<OptionContractDescriptor>();
            _activeOptionStrikes = Array.Empty<decimal>();
            _activeOptionRequestedStrikes = Array.Empty<decimal>();
            _activeOptionSelectionAtmStrike = _activeAtmStrike = _candidateAtmStrike = 0;
            _regularTradeSamples.Clear();
            _allTradeSamples.Clear();
            _rollingFlow.Clear();
            _flowCoverageStartUtc = nowUtc;
            _fixedFlowLadderBucketStartUtc = DateTime.MinValue;
            _optionOpenInterest.Clear();
            _optionOiReceivedByContract.Clear();
            _optionOpenInterestReceivedUtc = DateTime.MinValue;
            _optionDataIsDelayed = false;
            _optionInputHealth = null;
            _oiCacheDirty = _oiCacheWriteFailed = false;
            _nextOiCacheSaveUtc = DateTime.MinValue;
            _oiRevision++;
            RefreshOiConfirmationEpoch(nowUtc);
            _nextOiRetryUtc = nowUtc;
            _optionOiFastRetryCount = 0;
        }
        // Clear old-target rows BEFORE asynchronous discovery can fail or block.
        if (_showOptionOpenInterest)
            SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot.Waiting(DateTime.MinValue, "等待目标到期日合约")
                with { Ticker = ticker, Expiration = expiration, Status = OptionDataStatus.WaitingChain });
        if (_showOptionPremiumFlow)
            SetOptionFlowSnapshot(OptionFlowSnapshot.Waiting(DateTime.MinValue, "等待目标到期日合约",
                _optionFlowBucketMode, _optionFlowTradeScope, _optionFlowIntervalMinutes)
                with { Ticker = ticker, Expiration = expiration, Status = OptionDataStatus.WaitingChain });

        // Disk reads are outside the callback/data lock.
        var cached = OptionOpenInterestCache.LoadSnapshot(cacheDirectory ?? OptionOpenInterestCache.GetDefaultDirectory(), ticker, expiration, nowUtc);
        lock (_optionDataSync)
        {
            foreach (var item in cached.Entries)
            {
                _optionOpenInterest[item.Key] = item.Value.OpenInterest;
                _optionOiReceivedByContract[item.Key] = item.Value.ReceivedUtc;
            }
            _optionOpenInterestReceivedUtc = cached.ReceivedUtc;
        }
        return true;
    }
}
