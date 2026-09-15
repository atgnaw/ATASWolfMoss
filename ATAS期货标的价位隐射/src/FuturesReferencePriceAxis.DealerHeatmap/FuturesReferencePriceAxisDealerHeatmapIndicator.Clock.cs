namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private TimeProvider _realtimeClock = TimeProvider.System;
    private readonly RealtimeClockGuard _flowClockGuard = new();
    private readonly FlowReceptionDiagnostics _flowReception = new();

    // External live data must not select buckets using chart/playback/server time.
    // The linked ordinary edition's mapping clock is deliberately unchanged.
    private DateTime RealtimeUtcNow() => _realtimeClock.GetUtcNow().UtcDateTime;

    private void CheckFlowClock(DateTime nowUtc)
    {
        lock (_optionDataSync)
        {
            _sharedFlow?.Book.ObserveClock(nowUtc, _realtimeClock.GetTimestamp(), _realtimeClock.TimestampFrequency);
            if (!_flowClockGuard.Observe(nowUtc, _realtimeClock.GetTimestamp(), _realtimeClock.TimestampFrequency)) return;
            _regularTradeSamples.Clear();
            _allTradeSamples.Clear();
            _rollingFlow.Clear();
            _flowCoverageStartUtc = nowUtc;
            _fixedFlowLadderBucketStartUtc = DateTime.MinValue;
            if (_activeOptionTicker != null && _activeOptionContracts.Count > 0)
                ConfigureRollingLadder(nowUtc);
            // Never retain a future completed frame following a backward correction.
            SetOptionFlowSnapshot(OptionFlowSnapshot.Waiting(nowUtc,
                "UTC 时钟调整；重新建立观察基线", _optionFlowBucketMode,
                _optionFlowTradeScope, _optionFlowIntervalMinutes)
                with { Ticker = _activeOptionTicker, Expiration = _activeOptionExpiration });
            _performance.RecordError("CLOCK_ADJUSTED", "CLOCK");
        }
    }

    private PerformanceSnapshot AddClockDiagnostics(PerformanceSnapshot snapshot)
    {
        DateTime? atasUtc = null;
        try
        {
            var value = UtcTime;
            if (value.Year >= 1970) atasUtc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
        catch { /* Unavailable without an attached chart. */ }
        lock (_optionDataSync)
        {
            var flow = Volatile.Read(ref _optionFlowSnapshot);
            return snapshot with
            {
                AtasUtc = atasUtc,
                FlowTicker = _activeOptionTicker,
                FlowExpiration = _activeOptionExpiration,
                Reception = _flowReception.Read(),
                FutureBuffer = _sharedFlow?.Book.FutureSnapshot(),
                ClockJumps = _flowClockGuard.JumpCount,
                ClockAdjustmentSeconds = _flowClockGuard.LastAdjustmentSeconds,
                CoverageStartUtc = _flowCoverageStartUtc == DateTime.MinValue ? null : _flowCoverageStartUtc,
                BucketStartUtc = flow.BucketStartUtc,
                BucketEndUtc = flow.BucketEndUtc
            };
        }
    }
}
