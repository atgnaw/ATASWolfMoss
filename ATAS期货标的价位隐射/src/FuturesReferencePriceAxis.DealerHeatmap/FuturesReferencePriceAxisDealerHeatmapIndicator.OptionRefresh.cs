namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private static readonly TimeSpan OptionLoopInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FixedBucketGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OptionOiBatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OptionOiFastRetryDelay = TimeSpan.FromSeconds(30);
    private const int OptionOiFastRetryLimit = 2;
    private readonly object _optionScheduleSync = new();
    private readonly object _optionDataSync = new();
    private readonly Dictionary<long, long> _optionOpenInterest = new();
    private readonly Dictionary<long, List<OptionCumulativeSample>> _regularTradeSamples = new();
    private readonly Dictionary<long, List<OptionCumulativeSample>> _allTradeSamples = new();
    private readonly OptionRollingFlowState _rollingFlow = new();
    private CancellationTokenSource? _optionLifetimeCancellation;
    private CancellationTokenSource? _optionScheduleCancellation;
    private Task _optionLoopTask = Task.CompletedTask;
    private bool _optionResetGatewayPending;
    private bool _optionResetFlowPending;
    private IbOptionGatewayPool.Lease? _optionGatewayLease;
    private IIbOptionSubscriptionLease? _optionSubscription;
    private OptionOpenInterestSnapshot _optionOpenInterestSnapshot =
        OptionOpenInterestSnapshot.Disabled(DateTime.MinValue);
    private OptionFlowSnapshot _optionFlowSnapshot =
        OptionFlowSnapshot.Disabled(
            DateTime.MinValue,
            OptionFlowBucketMode.PreviousCompletedFixed,
            OptionFlowTradeScope.RegularTrades,
            5);
    private IReadOnlyList<OptionContractDescriptor> _activeOptionContracts =
        Array.Empty<OptionContractDescriptor>();
    private IReadOnlyList<decimal> _activeOptionStrikes = Array.Empty<decimal>();
    private IReadOnlyList<decimal> _activeOptionRequestedStrikes = Array.Empty<decimal>();
    private string? _activeOptionTicker;
    private DateOnly? _activeOptionExpiration;
    private decimal _activeAtmStrike;
    private decimal _activeOptionSelectionAtmStrike;
    private decimal _candidateAtmStrike;
    private DateTime _candidateAtmSinceUtc;
    private DateTime _lastAtmSwitchUtc;
    private DateTime _flowCoverageStartUtc;
    private DateTime _fixedFlowLadderBucketStartUtc = DateTime.MinValue;
    private DateTime _nextOiRetryUtc = DateTime.MinValue;
    private DateTime _optionOpenInterestReceivedUtc = DateTime.MinValue;
    private int _optionOiFastRetryCount;
    private bool _optionDataIsDelayed;
    private bool _oiCacheDirty;
    private long _optionDataGeneration;

    private void InitializeOptionData()
    {
        _optionLifetimeCancellation = new CancellationTokenSource();
        RestartOptionDataSchedule();
    }

    private void RestartOptionDataSchedule(bool resetGateway = false, bool resetFlowAggregation = false)
    {
        lock (_optionScheduleSync)
        {
            if (!IsIndicatorInitialized || _optionLifetimeCancellation == null)
                return;

            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation = null;
            _optionResetGatewayPending |= resetGateway;
            _optionResetFlowPending |= resetFlowAggregation;
            var generation = Interlocked.Increment(ref _optionDataGeneration);
            var nowUtc = CurrentUtcTime();

            if (!_showOptionOpenInterest)
                SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot.Disabled(nowUtc));

            if (!_showOptionPremiumFlow)
            {
                SetOptionFlowSnapshot(OptionFlowSnapshot.Disabled(
                    nowUtc,
                    _optionFlowBucketMode,
                    _optionFlowTradeScope,
                    _optionFlowIntervalMinutes));
            }

            if (!_showOptionOpenInterest && !_showOptionPremiumFlow)
            {
                var previous = _optionLoopTask;
                _optionLoopTask = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    await ReleaseOptionGatewayAsync().ConfigureAwait(false);
                });
                return;
            }

            _optionScheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _optionLifetimeCancellation.Token);
            var source = _optionScheduleCancellation;
            var predecessor = _optionLoopTask;
            _optionLoopTask = Task.Run(() => RunOptionGenerationAsync(predecessor, generation, source));
        }
    }

    private async Task RunOptionGenerationAsync(Task previous, long generation, CancellationTokenSource source)
    {
        var token = source.Token;
        var started = false;
        try
        {
            try { await previous.ConfigureAwait(false); } catch { }
            token.ThrowIfCancellationRequested();
            bool resetGateway, resetFlow;
            lock (_optionScheduleSync)
            {
                token.ThrowIfCancellationRequested();
                resetGateway = _optionResetGatewayPending;
                resetFlow = _optionResetFlowPending;
                _optionResetGatewayPending = _optionResetFlowPending = false;
            }
            if (resetGateway) await ReleaseOptionGatewayAsync().ConfigureAwait(false);
            if (resetFlow) ResetOptionFlowAggregation();
            started = true;
            await RunOptionDataLoopAsync(generation, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (started)
            {
                SuspendOptionObservation(CurrentUtcTime());
                Interlocked.Exchange(ref _optionSubscription, null)?.Dispose();
                SaveOpenInterestCacheIfNeeded();
            }
            lock (_optionScheduleSync)
                if (ReferenceEquals(_optionScheduleCancellation, source)) _optionScheduleCancellation = null;
            source.Dispose();
        }
    }

    private void PauseOptionDataSchedule()
    {
        Interlocked.Increment(ref _optionDataGeneration);

        lock (_optionScheduleSync)
        {
            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation = null;
        }
    }

    private void StopOptionData()
    {
        Interlocked.Increment(ref _optionDataGeneration);

        lock (_optionScheduleSync)
        {
            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation = null;
            var previous = _optionLoopTask;
            _optionLoopTask = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { }
                await ReleaseOptionGatewayAsync().ConfigureAwait(false);
            });
        }

        _optionLifetimeCancellation?.Cancel();
        _optionLifetimeCancellation?.Dispose();
        _optionLifetimeCancellation = null;
    }

    private async Task ReleaseOptionGatewayAsync()
    {
        var lease = Interlocked.Exchange(ref _optionGatewayLease, null);

        if (lease != null)
            await lease.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunOptionDataLoopAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        using var diagnosticTask = _performance.TrackTask();
        var retryIndex = 0;
        var retryDelays = new[] { 2, 5, 15, 30, 60 };

        while (!cancellationToken.IsCancellationRequested
               && generation == Interlocked.Read(ref _optionDataGeneration))
        {
            try
            {
                await EnsureOptionTargetAsync(generation, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                PublishOptionSnapshots();
                SaveOpenInterestCacheIfNeeded();
                retryIndex = 0;
                await Task.Delay(OptionLoopInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IbOptionGatewayException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _performance.RecordError(exception.Code);
                PublishOptionError(exception);
                ReleaseOptionGatewayAfterFailure(exception.Code);
                var seconds = retryDelays[Math.Min(retryIndex++, retryDelays.Length - 1)];
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReleaseOptionGatewayAsync().ConfigureAwait(false);
                PublishOptionError(new IbOptionGatewayException(
                    "INTERNAL", "IB 期权数据内部错误"));
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureOptionTargetAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        if (!InstrumentPairResolver.TryResolve(
                ConfiguredPairMode,
                InstrumentInfo?.Instrument,
                out var pair)
            || !OptionUnderlyingProfile.TryResolve(pair.ReferenceSymbol, out var profile))
        {
            throw new IbOptionGatewayException(
                "NO_CONTRACTS", "等待可识别的 NQ/MNQ 或 ES/MES 品种");
        }

        var nowUtc = CurrentUtcTime();
        if (nowUtc.Year < 1970) { SetOptionWaiting("等待有效 UTC 时钟"); return; }
        var expiration = NyseTradingCalendar.ResolveTarget(nowUtc, profile.Ticker).TargetExpiration;
        var targetChanged = TransitionOptionTarget(profile.Ticker, expiration, nowUtc);
        lock (_optionDataSync)
            if (_showOptionOpenInterest) RefreshOiConfirmationEpoch(nowUtc);
        var ratio = CurrentEffectiveMappingRatio;
        var futuresPrice = LatestChartPrice;

        if (ratio is not > 0m || futuresPrice <= 0m)
        {
            SetOptionWaiting("等待有效映射比例与期货现价");
            return;
        }

        var gateway = GetOrCreateOptionGateway();
        await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var available = await gateway.GetAvailableStrikesAsync(
                profile, expiration, cancellationToken)
            .ConfigureAwait(false);
        var mappedSpot = futuresPrice / ratio.Value;
        var knownTargetHours = _activeOptionTicker == profile.Ticker
            && _activeOptionExpiration == expiration && _activeOptionContracts.Count > 0;
        var needsFlowLines = _showOptionPremiumFlow && (!knownTargetHours
            || IsInsideOptionFlowSubscriptionWindow(nowUtc, profile.Ticker, _activeOptionContracts));
        var flowLevels = _optionGatewayLease!.SetFlowDemand(profile.Ticker,
            needsFlowLines ? _optionStrikeLevels : 0, _ibOptionMarketDataLineBudget);
        var requestedLevels = needsFlowLines ? flowLevels : _optionStrikeLevels;
        if (requestedLevels == 0)
            throw new IbOptionGatewayException("LINE_LIMIT", "共享预算不足以保留各标的 ATM");
        var selected = OptionStrikeSelection.SelectCenteredCandidates(
            available,
            mappedSpot,
            requestedLevels,
            out var candidateAtm);
        selected = OptionStrikeSelection.ApplySymmetricBudget(
            selected,
            candidateAtm,
            _ibOptionMarketDataLineBudget);

        if (selected.Count == 0)
            throw new IbOptionGatewayException("NO_CONTRACTS", "ATM 附近没有可用 0DTE 执行价");

        var atmChanged = candidateAtm != _activeOptionSelectionAtmStrike;
        var rollingMode = _showOptionPremiumFlow
                          && _optionFlowBucketMode == OptionFlowBucketMode.Rolling;

        if (!targetChanged && rollingMode)
        {
            bool holdLadder;
            lock (_optionDataSync)
            {
                _rollingFlow.AdvanceClock(nowUtc);
                holdLadder = _rollingFlow.IsLadderLocked(nowUtc);
            }
            if (holdLadder)
            {
                await MaintainOptionSubscriptionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!rollingMode && !targetChanged && atmChanged && _activeOptionSelectionAtmStrike > 0m)
        {
            if (_candidateAtmStrike != candidateAtm)
            {
                _candidateAtmStrike = candidateAtm;
                _candidateAtmSinceUtc = nowUtc;
                return;
            }

            if (nowUtc - _candidateAtmSinceUtc < TimeSpan.FromSeconds(15)
                || nowUtc - _lastAtmSwitchUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }
        }

        var ladderChanged = targetChanged
                            || candidateAtm != _activeOptionSelectionAtmStrike
                            || !selected.SequenceEqual(_activeOptionRequestedStrikes);

        if (ladderChanged)
        {
            if (!targetChanged && ShouldHoldCurrentFixedFlowLadder(nowUtc))
            {
                await MaintainOptionSubscriptionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!targetChanged
                && _showOptionPremiumFlow
                && _optionFlowBucketMode == OptionFlowBucketMode.PreviousCompletedFixed)
            {
                // Publish the completed bucket with its locked ladder before
                // replacing the edge contracts for the next bucket.
                PublishOptionSnapshots();
            }

            await ConfigureOptionLadderAsync(
                    profile,
                    expiration,
                    candidateAtm,
                    selected,
                    generation,
                    cancellationToken)
                .ConfigureAwait(false);
            _lastAtmSwitchUtc = nowUtc;
            _candidateAtmStrike = candidateAtm;
            _candidateAtmSinceUtc = nowUtc;
        }
        else
        {
            if (rollingMode)
            {
                lock (_optionDataSync)
                {
                    if (!_rollingFlow.HasLadder)
                        ConfigureRollingLadder(nowUtc);
                }
            }
            await MaintainOptionSubscriptionAsync(cancellationToken).ConfigureAwait(false);
            AdvanceFixedFlowBucketLock(nowUtc);
            if (rollingMode)
            {
                lock (_optionDataSync)
                    _rollingFlow.RenewAtmLock(_activeAtmStrike, nowUtc, _optionFlowIntervalMinutes);
            }

            if (_showOptionOpenInterest
                 && _optionSubscription == null
                 && nowUtc >= _nextOiRetryUtc
                 && HasMissingOpenInterest())
            {
                await FetchOpenInterestInBatchesAsync(
                    _activeOptionContracts,
                    cancellationToken)
                .ConfigureAwait(false);
                ScheduleNextOiRetry(CurrentUtcTime());
            }
        }
    }

    private async Task ConfigureOptionLadderAsync(
        OptionUnderlyingProfile profile,
        DateOnly expiration,
        decimal atmStrike,
        IReadOnlyList<decimal> strikes,
        long generation,
        CancellationToken cancellationToken)
    {
        var gateway = GetOrCreateOptionGateway();
        var available = await gateway.GetAvailableStrikesAsync(
                profile, expiration, cancellationToken)
            .ConfigureAwait(false);
        var probeCount = Math.Min(available.Count, strikes.Count + 16);
        var probeStrikes = OptionStrikeSelection.SelectCenteredCandidates(
            available,
            atmStrike,
            probeCount,
            out _);
        var probedContracts = await gateway.ResolveContractsAsync(
                profile, expiration, probeStrikes, cancellationToken)
            .ConfigureAwait(false);
        var resolvedStrikes = probedContracts
            .Select(static contract => contract.StrikeUsd)
            .Distinct()
            .OrderBy(static strike => strike)
            .ToArray();
        var finalStrikes = OptionStrikeSelection.SelectCenteredCandidates(
            resolvedStrikes,
            atmStrike,
            strikes.Count,
            out var resolvedAtmStrike);
        var finalStrikeSet = finalStrikes.ToHashSet();
        var contracts = probedContracts
            .Where(contract => finalStrikeSet.Contains(contract.StrikeUsd))
            .ToArray();

        if (generation != Interlocked.Read(ref _optionDataGeneration))
            return;
        cancellationToken.ThrowIfCancellationRequested();

        if (contracts.Length == 0)
            throw new IbOptionGatewayException("NO_CONTRACTS", "IB 未解析到可订阅的期权合约");

        var configuredUtc = CurrentUtcTime();
        var fixedBucketStartUtc = TryGetFixedFlowBucketStart(
            configuredUtc,
            profile.Ticker,
            contracts,
            out var bucketStartUtc)
            ? bucketStartUtc
            : DateTime.MinValue;

        lock (_optionDataSync)
        {
            var targetChanged = !string.Equals(_activeOptionTicker, profile.Ticker,
                                    StringComparison.OrdinalIgnoreCase)
                                || _activeOptionExpiration != expiration;
            _activeOptionTicker = profile.Ticker;
            _activeOptionExpiration = expiration;
            _activeAtmStrike = resolvedAtmStrike;
            _activeOptionSelectionAtmStrike = atmStrike;
            _activeOptionRequestedStrikes = strikes.ToArray();
            _activeOptionStrikes = finalStrikes.ToArray();
            _activeOptionContracts = contracts;
            _optionOiFastRetryCount = 0;
            _optionDataIsDelayed = false;
            _fixedFlowLadderBucketStartUtc = fixedBucketStartUtc;

            if (targetChanged)
            {
                _rollingFlow.Clear();
                _regularTradeSamples.Clear();
                _allTradeSamples.Clear();
                _flowCoverageStartUtc = configuredUtc;
                _optionOpenInterest.Clear();
                _optionOiReceivedByContract.Clear();
                _oiRevision++;
                _optionOpenInterestReceivedUtc = DateTime.MinValue;
            }
            else
            {
                var activeConIds = contracts
                    .Select(static contract => contract.ConId)
                    .ToHashSet();
                PruneFlowSamples(_regularTradeSamples, activeConIds);
                PruneFlowSamples(_allTradeSamples, activeConIds);

                if (_flowCoverageStartUtc == DateTime.MinValue)
                    _flowCoverageStartUtc = configuredUtc;
            }

            if (_showOptionPremiumFlow && _optionFlowBucketMode == OptionFlowBucketMode.Rolling)
                ConfigureRollingLadder(configuredUtc);
        }

        await MaintainOptionSubscriptionAsync(cancellationToken).ConfigureAwait(false);

        if (_showOptionOpenInterest && _optionSubscription == null)
            await FetchOpenInterestInBatchesAsync(contracts, cancellationToken)
                .ConfigureAwait(false);

        ScheduleNextOiRetry(CurrentUtcTime());
    }

    // Caller holds _optionDataSync; keep contract discovery outside this lock.
    private void ConfigureRollingLadder(DateTime nowUtc)
    {
        var segments = _activeOptionContracts.Count == 0
            ? Array.Empty<OptionTradingSegment>()
            : IbTradingHoursParser.Parse(
                _activeOptionContracts[0].TradingHours,
                _activeOptionContracts[0].TradingTimeZoneId,
                string.Equals(_activeOptionTicker, "SPX", StringComparison.OrdinalIgnoreCase));
        _rollingFlow.SetTradingSegments(segments, nowUtc);
        _rollingFlow.ConfigureLadder(_activeOptionContracts, _activeAtmStrike,
            nowUtc, _optionFlowIntervalMinutes);
    }

    private async Task FetchOpenInterestInBatchesAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        CancellationToken cancellationToken)
    {
        var gateway = GetOrCreateOptionGateway();
        var pending = contracts.Where(contract =>
        {
            lock (_optionDataSync)
                return NeedsOiConfirmation(contract.ConId);
        })
            .OrderBy(contract => Math.Abs(contract.StrikeUsd - _activeAtmStrike))
            .ThenBy(static contract => contract.StrikeUsd)
            .ThenBy(static contract => contract.Right)
            .ToList();

        while (pending.Count > 0)
        {
            var remaining = new HashSet<long>(
                pending.Select(static contract => contract.ConId));
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnBatchUpdate(IbOptionMarketDataUpdate update)
            {
                if (cancellationToken.IsCancellationRequested) return;
                OnScheduledOptionMarketData(update, cancellationToken);

                if (update.OpenInterest is >= 0)
                {
                    lock (remaining)
                    {
                        remaining.Remove(update.Contract.ConId);

                        if (remaining.Count == 0)
                            completion.TrySetResult();
                    }
                }
            }

            using var lease = await gateway.SubscribeAvailableAsync(
                    pending,
                    new IbOptionSubscriptionRequirements(true, false, false),
                    OnBatchUpdate,
                    _ibOptionMarketDataLineBudget,
                    cancellationToken)
                .ConfigureAwait(false);
            var leasedIds = lease.ContractIds.ToHashSet();

            lock (remaining)
            {
                remaining.IntersectWith(leasedIds);

                if (remaining.Count == 0)
                    completion.TrySetResult();
            }

            await Task.WhenAny(
                    completion.Task,
                    Task.Delay(OptionOiBatchTimeout, cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            pending.RemoveAll(contract => leasedIds.Contains(contract.ConId));
        }
    }

    private void OnOptionMarketData(IbOptionMarketDataUpdate update)
        => OnScheduledOptionMarketData(update, default);

    private void OnScheduledOptionMarketData(IbOptionMarketDataUpdate update, CancellationToken cancellationToken)
    {
        using var diagnosticMeasurement = _performance.Measure(PerformanceMetric.Callback);
        if (cancellationToken.IsCancellationRequested) return;
        _performance.EventReceived();
        if (!string.IsNullOrWhiteSpace(update.ErrorCode))
        {
            lock (_optionDataSync)
                if (update.Contract.Ticker != _activeOptionTicker || update.Contract.Expiration != _activeOptionExpiration) return;
            _performance.RecordError(update.ErrorCode);
            PublishOptionError(new IbOptionGatewayException(
                update.ErrorCode,
                update.ErrorMessage ?? "IB 期权行情订阅失败"));
            return;
        }

        lock (_optionDataSync)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (!string.Equals(update.Contract.Ticker, _activeOptionTicker,
                    StringComparison.OrdinalIgnoreCase)
                || update.Contract.Expiration != _activeOptionExpiration)
            {
                return;
            }

            _optionDataIsDelayed |= update.IsDelayed;

            if (update.OpenInterest.HasValue && update.OpenInterest.Value >= 0
                && update.ReceivedUtc.Kind == DateTimeKind.Utc && update.ReceivedUtc <= DateTime.UtcNow
                && (!_optionOiReceivedByContract.TryGetValue(update.Contract.ConId, out var priorReceived)
                    || update.ReceivedUtc >= priorReceived))
            {
                if (!_optionOpenInterest.TryGetValue(update.Contract.ConId, out var previousOi)
                    || previousOi != update.OpenInterest.Value || NeedsOiConfirmation(update.Contract.ConId))
                {
                    _oiRevision++;
                    _optionOpenInterest[update.Contract.ConId] = update.OpenInterest.Value;
                    _optionOiReceivedByContract[update.Contract.ConId] = update.ReceivedUtc;
                    if (update.ReceivedUtc > _optionOpenInterestReceivedUtc)
                        _optionOpenInterestReceivedUtc = update.ReceivedUtc;
                    _oiCacheDirty = true;
                }
            }

            if (_optionFlowBucketMode == OptionFlowBucketMode.Rolling)
            {
                if (update.RegularTrades.HasValue)
                    _rollingFlow.Add(update.RegularTrades.Value,
                        OptionFlowTradeScope.RegularTrades, update.ReceivedUtc);
                if (update.AllTimeAndSales.HasValue)
                    _rollingFlow.Add(update.AllTimeAndSales.Value,
                        OptionFlowTradeScope.AllTimeAndSales, update.ReceivedUtc);
            }
            else
            {
                if (update.RegularTrades.HasValue && AcceptFixedSample(update.RegularTrades.Value, update.ReceivedUtc))
                    AddCumulativeSample(_regularTradeSamples, update.RegularTrades.Value);
                if (update.AllTimeAndSales.HasValue && AcceptFixedSample(update.AllTimeAndSales.Value, update.ReceivedUtc))
                    AddCumulativeSample(_allTradeSamples, update.AllTimeAndSales.Value);
            }
        }

        // Raw samples are not rendered directly. The scheduled immutable snapshot
        // publication triggers redraw; avoid a redundant redraw for every IB tick.
    }

    private static void AddCumulativeSample(
        Dictionary<long, List<OptionCumulativeSample>> destination,
        OptionCumulativeSample sample)
    {
        if (!sample.HasValidCumulativeValue()) return;
        if (!destination.TryGetValue(sample.ConId, out var samples))
        {
            samples = new List<OptionCumulativeSample>();
            destination.Add(sample.ConId, samples);
        }

        if (samples.Count > 0 && sample.SampleUtc < samples[^1].SampleUtc) return;
        if (samples.Count > 0
            && (sample.TotalVolume < samples[^1].TotalVolume
                || sample.Multiplier != samples[^1].Multiplier
                || sample.CumulativePremium < samples[^1].CumulativePremium))
        {
            samples.Clear();
        }

        if (samples.Count > 0 && samples[^1].SampleUtc == sample.SampleUtc)
            samples[^1] = sample;
        else
            samples.Add(sample);

        var cutoff = sample.SampleUtc - TimeSpan.FromHours(2);
        var expired = OptionFlowAggregation.LowerBound(samples, cutoff);
        if (expired > 0) samples.RemoveRange(0, expired);
    }

    private void PublishOptionSnapshots()
        => PublishOptionSnapshotsAt(CurrentUtcTime());

    // Explicit clock seam also used by deterministic offline publication benchmarks.
    private void PublishOptionSnapshotsAt(DateTime nowUtc)
    {
        using var diagnosticMeasurement = _performance.Measure(PerformanceMetric.Publish);
        IReadOnlyList<OptionContractDescriptor> contracts;
        IReadOnlyList<decimal> strikes;
        OptionOpenInterestSnapshot? oiSnapshot = null;
        OptionFlowSnapshot? flowSnapshot = null;
        string? ticker;
        DateOnly? expiration;
        decimal atm;
        DateTime coverageStart;
        DateTime oiReceivedUtc;
        bool delayed;

        lock (_optionDataSync)
        {
            if (_showOptionOpenInterest) RefreshOiConfirmationEpoch(nowUtc);
            contracts = _activeOptionContracts;
            strikes = _activeOptionStrikes;
            var source = _optionFlowTradeScope == OptionFlowTradeScope.RegularTrades
                ? _regularTradeSamples
                : _allTradeSamples;
            ticker = _activeOptionTicker;
            expiration = _activeOptionExpiration;
            atm = _activeAtmStrike;
            coverageStart = _flowCoverageStartUtc;
            oiReceivedUtc = _optionOpenInterestReceivedUtc;
            delayed = _optionDataIsDelayed;
            if (ticker != null && expiration.HasValue && strikes.Count > 0)
            {
                if (_showOptionOpenInterest)
                    oiSnapshot = GetPublishedOiSnapshot(ticker, expiration.Value, atm, strikes, contracts, oiReceivedUtc);
                if (_showOptionPremiumFlow)
                    flowSnapshot = CreateFlowSnapshot(ticker, expiration.Value, atm, strikes, contracts,
                        source, nowUtc, coverageStart, delayed);
            }
            if (_performance.Enabled)
                _performance.SetCacheCounts(
                    _regularTradeSamples.Values.Sum(static values => (long)values.Count)
                    + _allTradeSamples.Values.Sum(static values => (long)values.Count)
                    + _rollingFlow.CachedSampleCount, _optionOpenInterest.Count);
        }

        if (ticker == null || !expiration.HasValue || strikes.Count == 0)
            return;

        if (oiSnapshot != null) SetOptionOpenInterestSnapshot(oiSnapshot);
        if (flowSnapshot != null) SetOptionFlowSnapshot(flowSnapshot);
        var activeLines = GetOptionActiveLineCount();
        if (activeLines != _publishedOptionLineCount)
        {
            _publishedOptionLineCount = activeLines;
            RequestRedraw();
        }
    }

    private OptionFlowSnapshot CreateFlowSnapshot(
        string ticker,
        DateOnly expiration,
        decimal atm,
        IReadOnlyList<decimal> strikes,
        IReadOnlyList<OptionContractDescriptor> contracts,
        IReadOnlyDictionary<long, List<OptionCumulativeSample>> samples,
        DateTime nowUtc,
        DateTime coverageStartUtc,
        bool delayed)
    {
        if (_optionFlowBucketMode == OptionFlowBucketMode.Rolling)
        {
            lock (_optionDataSync)
                return _rollingFlow.CreateSnapshot(ticker, expiration, nowUtc,
                    _optionFlowIntervalMinutes, _optionFlowTradeScope, _optionStrikeLevels, delayed);
        }

        var segments = GetCachedFlowSegments(ticker, contracts);
        var segment = segments.LastOrDefault(value =>
            value.Contains(nowUtc) || value.EndUtc <= nowUtc);
        DateTime? startUtc = null;
        DateTime? endUtc = null;
        var values = new Dictionary<long, OptionIntervalValue>();

        if (_optionFlowBucketMode == OptionFlowBucketMode.PreviousCompletedFixed
            && segment != default)
        {
            var bucket = OptionFlowAggregation.GetPreviousCompletedBucket(
                nowUtc, segment, _optionFlowIntervalMinutes, FixedBucketGrace);

            if (bucket.HasValue)
            {
                var current = Volatile.Read(ref _optionFlowSnapshot);

                if (string.Equals(current.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
                    && current.Expiration == expiration
                    && current.BucketMode == _optionFlowBucketMode
                    && current.TradeScope == _optionFlowTradeScope
                    && current.IntervalMinutes == _optionFlowIntervalMinutes
                    && current.BucketEndUtc >= bucket.Value.EndUtc)
                {
                    if (current.Status is OptionDataStatus.Live or OptionDataStatus.Closed or OptionDataStatus.Delayed or OptionDataStatus.Frozen)
                    {
                        var state = delayed ? OptionDataStatus.Delayed
                            : segment.Contains(nowUtc) ? OptionDataStatus.Live : OptionDataStatus.Closed;
                        return current.Status == state ? current : current with { Status = state };
                    }
                    return current;
                }

                startUtc = bucket.Value.StartUtc;
                endUtc = bucket.Value.EndUtc;
                PopulateIntervalValues(
                    samples,
                    startUtc.Value,
                    endUtc.Value,
                    allowPartial: true,
                    values,
                    segment.StartUtc > coverageStartUtc ? segment.StartUtc : coverageStartUtc);
            }
        }

        var rows = CreateRows(strikes, contracts, null, values);
        var isOpen = segments.Any(value => value.Contains(nowUtc));
        var isInitialPartialBucket = startUtc.HasValue
                                     && coverageStartUtc > startUtc.Value
                                     && values.Count == 0;
        var status = delayed
            ? OptionDataStatus.Delayed
            : !isOpen ? OptionDataStatus.Closed
            : !startUtc.HasValue || !endUtc.HasValue || isInitialPartialBucket
                ? OptionDataStatus.Warming
                : isOpen
                    ? OptionDataStatus.Live
                    : OptionDataStatus.Closed;
        var partialCount = values.Count(static item => item.Value.IsPartial);
        var message = status == OptionDataStatus.Closed && !startUtc.HasValue
            ? "非交易区段；等待下一开放区段"
            : status == OptionDataStatus.Warming
            ? "等待第一个完整时间桶"
            : $"{_optionFlowIntervalMinutes}m 已完成桶；数据 {values.Count}/{contracts.Count}"
              + (partialCount > 0 ? $"；部分 {partialCount}" : string.Empty);
        return new OptionFlowSnapshot(
            ticker,
            expiration,
            atm,
            rows,
            status,
            startUtc,
            endUtc,
            nowUtc,
            message,
            _optionFlowBucketMode,
            _optionFlowTradeScope,
            _optionFlowIntervalMinutes,
            _optionStrikeLevels,
            strikes.Count);
    }

    private static void PopulateIntervalValues(
        IReadOnlyDictionary<long, List<OptionCumulativeSample>> samples,
        DateTime startUtc,
        DateTime endUtc,
        bool allowPartial,
        Dictionary<long, OptionIntervalValue> destination,
        DateTime earliestBaselineUtc)
    {
        foreach (var item in samples)
        {
            if (OptionFlowAggregation.TryCalculateBucketValue(
                    item.Value,
                    startUtc,
                    endUtc,
                    allowPartial,
                    out var value, earliestBaselineUtc))
            {
                destination[item.Key] = value;
            }
        }
    }

    private static IReadOnlyList<OptionStrikeRow> CreateRows(
        IReadOnlyList<decimal> strikes,
        IReadOnlyList<OptionContractDescriptor> contracts,
        IReadOnlyDictionary<long, long>? oi,
        IReadOnlyDictionary<long, OptionIntervalValue>? flow)
    {
        var byKey = contracts.ToDictionary(
            static contract => (contract.StrikeUsd, contract.Right));
        var rows = new List<OptionStrikeRow>(strikes.Count);

        foreach (var strike in strikes)
        {
            byKey.TryGetValue((strike, OptionRight.Call), out var call);
            byKey.TryGetValue((strike, OptionRight.Put), out var put);
            var hasCall = call.ConId > 0;
            var hasPut = put.ConId > 0;
            long callOi = 0;
            long putOi = 0;
            var callFlow = OptionIntervalValue.Zero;
            var putFlow = OptionIntervalValue.Zero;
            oi?.TryGetValue(call.ConId, out callOi);
            oi?.TryGetValue(put.ConId, out putOi);
            flow?.TryGetValue(call.ConId, out callFlow);
            flow?.TryGetValue(put.ConId, out putFlow);
            rows.Add(new OptionStrikeRow(
                strike,
                hasCall && oi != null && oi.ContainsKey(call.ConId) ? callOi : null,
                hasPut && oi != null && oi.ContainsKey(put.ConId) ? putOi : null,
                hasCall && flow != null && flow.ContainsKey(call.ConId)
                    ? callFlow.Premium : null,
                hasPut && flow != null && flow.ContainsKey(put.ConId)
                    ? putFlow.Premium : null,
                hasCall && flow != null && flow.ContainsKey(call.ConId)
                    ? callFlow.Volume : null,
                hasPut && flow != null && flow.ContainsKey(put.ConId)
                    ? putFlow.Volume : null,
                hasCall && flow != null && flow.ContainsKey(call.ConId)
                    ? callFlow.ObservedStartUtc : null,
                hasPut && flow != null && flow.ContainsKey(put.ConId)
                    ? putFlow.ObservedStartUtc : null,
                hasCall && flow != null && flow.ContainsKey(call.ConId)
                    && callFlow.IsPartial,
                hasPut && flow != null && flow.ContainsKey(put.ConId)
                    && putFlow.IsPartial));
        }

        return rows;
    }

    private bool ShouldHoldCurrentFixedFlowLadder(DateTime nowUtc)
    {
        IReadOnlyList<OptionContractDescriptor> contracts;
        string? ticker;
        DateTime activeBucketStartUtc;

        lock (_optionDataSync)
        {
            contracts = _activeOptionContracts.ToArray();
            ticker = _activeOptionTicker;
            activeBucketStartUtc = _fixedFlowLadderBucketStartUtc;
        }

        if (!_showOptionPremiumFlow
            || _optionFlowBucketMode != OptionFlowBucketMode.PreviousCompletedFixed
            || ticker == null
            || !TryGetFixedFlowBucketStart(
                nowUtc, ticker, contracts, out var currentBucketStartUtc))
        {
            return false;
        }

        if (activeBucketStartUtc == DateTime.MinValue)
        {
            lock (_optionDataSync)
                _fixedFlowLadderBucketStartUtc = currentBucketStartUtc;

            return true;
        }

        return OptionFlowAggregation.ShouldHoldFixedLadder(
            activeBucketStartUtc,
            currentBucketStartUtc,
            nowUtc,
            FixedBucketGrace);
    }

    private bool TryGetFixedFlowBucketStart(
        DateTime nowUtc,
        string ticker,
        IReadOnlyList<OptionContractDescriptor> contracts,
        out DateTime bucketStartUtc)
    {
        bucketStartUtc = DateTime.MinValue;

        if (!_showOptionPremiumFlow
            || _optionFlowBucketMode != OptionFlowBucketMode.PreviousCompletedFixed
            || contracts.Count == 0)
        {
            return false;
        }

        var segment = IbTradingHoursParser.Parse(
                contracts[0].TradingHours,
                contracts[0].TradingTimeZoneId,
                string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(value => value.Contains(nowUtc));

        if (segment == default)
            return false;

        bucketStartUtc = OptionFlowAggregation.GetFixedBucketStart(
            nowUtc,
            segment,
            _optionFlowIntervalMinutes);
        return true;
    }

    private static void PruneFlowSamples(
        Dictionary<long, List<OptionCumulativeSample>> samples,
        IReadOnlySet<long> activeConIds)
    {
        foreach (var conId in samples.Keys
                     .Where(conId => !activeConIds.Contains(conId))
                     .ToArray())
        {
            samples.Remove(conId);
        }
    }

    private void AdvanceFixedFlowBucketLock(DateTime nowUtc)
    {
        IReadOnlyList<OptionContractDescriptor> contracts;
        string? ticker;

        lock (_optionDataSync)
        {
            contracts = _activeOptionContracts.ToArray();
            ticker = _activeOptionTicker;
        }

        if (ticker == null
            || !TryGetFixedFlowBucketStart(
                nowUtc, ticker, contracts, out var currentBucketStartUtc)
            || nowUtc < currentBucketStartUtc + FixedBucketGrace)
        {
            return;
        }

        lock (_optionDataSync)
            _fixedFlowLadderBucketStartUtc = currentBucketStartUtc;
    }

    private void ResetOptionFlowAggregation()
    {
        lock (_optionDataSync)
        {
            _rollingFlow.Clear();
            _regularTradeSamples.Clear();
            _allTradeSamples.Clear();
            _flowCoverageStartUtc = DateTime.MinValue;
            _fixedFlowLadderBucketStartUtc = DateTime.MinValue;
            _optionDataIsDelayed = false;
        }

        if (_showOptionPremiumFlow)
        {
            SetOptionFlowSnapshot(OptionFlowSnapshot.Waiting(
                CurrentUtcTime(),
                "等待新的完整时间桶",
                _optionFlowBucketMode,
                _optionFlowTradeScope,
                _optionFlowIntervalMinutes));
        }
    }

    private void ReleaseOptionGatewayAfterFailure(string errorCode)
    {
        if (errorCode is "NO_PERMISSION" or "LINE_LIMIT" or "NO_CONTRACTS")
            return;

        SuspendOptionObservation(CurrentUtcTime());
        _optionSubscription?.Dispose();
        _optionSubscription = null;
        _ = ReleaseOptionGatewayAsync();
    }

    private int GetOptionActiveLineCount()
    {
        try
        {
            return Volatile.Read(ref _optionGatewayLease)?.Client.ActiveMarketDataLines ?? 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private IIbOptionGatewayClient GetOrCreateOptionGateway()
    {
        var lease = Volatile.Read(ref _optionGatewayLease);

        if (lease != null)
            return lease.Client;

        var created = IbOptionGatewayPool.Acquire(new IbGatewayConnectionOptions(
            _ibGatewayHost, _ibGatewayPort, _ibGatewayClientId));
        var existing = Interlocked.CompareExchange(
            ref _optionGatewayLease, created, null);

        if (existing != null)
        {
            _ = created.DisposeAsync();
            return existing.Client;
        }

        return created.Client;
    }

    private bool HasMissingOpenInterest()
    {
        lock (_optionDataSync)
            return _activeOptionContracts.Any(contract =>
                NeedsOiConfirmation(contract.ConId));
    }

    private void ScheduleNextOiRetry(DateTime nowUtc)
    {
        if (!HasMissingOpenInterest())
        {
            _optionOiFastRetryCount = 0;
            _nextOiRetryUtc = CalculateNextOiRetryUtc(nowUtc);
            return;
        }

        if (_optionOiFastRetryCount < OptionOiFastRetryLimit)
        {
            _optionOiFastRetryCount++;
            _nextOiRetryUtc = nowUtc + OptionOiFastRetryDelay;
            return;
        }

        _nextOiRetryUtc = CalculateNextOiRetryUtc(nowUtc);
    }

    private static DateTime CalculateNextOiRetryUtc(DateTime nowUtc)
        => OptionOiFreshnessPolicy.NextRetryUtc(nowUtc);

    private void SaveOpenInterestCacheIfNeeded()
    {
        string? ticker;
        DateOnly? expiration;
        Dictionary<long, OptionOiCacheEntry> values;
        var nowUtc = CurrentUtcTime();

        lock (_optionDataSync)
        {
            if (!_oiCacheDirty || nowUtc.Year < 1970 || nowUtc < _nextOiCacheSaveUtc
                || _activeOptionTicker == null || !_activeOptionExpiration.HasValue)
                return;

            _oiCacheDirty = false;
            ticker = _activeOptionTicker;
            expiration = _activeOptionExpiration;
            values = _optionOiReceivedByContract.Where(item => _optionOpenInterest.ContainsKey(item.Key))
                .ToDictionary(static item => item.Key, item => new OptionOiCacheEntry(_optionOpenInterest[item.Key], item.Value));
        }

        try
        {
            OptionOpenInterestCache.SaveEntries(
                OptionOpenInterestCache.GetDefaultDirectory(),
                ticker,
                expiration.Value,
                values, nowUtc);
            lock (_optionDataSync)
            {
                if (_oiCacheWriteFailed) _oiRevision++;
                _oiCacheWriteFailed = false;
            }
        }
        catch
        {
            lock (_optionDataSync)
            {
                if (_activeOptionTicker == ticker && _activeOptionExpiration == expiration)
                {
                    _oiCacheDirty = true;
                    _nextOiCacheSaveUtc = nowUtc.AddSeconds(30);
                    if (!_oiCacheWriteFailed) _oiRevision++;
                    _oiCacheWriteFailed = true;
                }
            }
        }
    }

    private void SetOptionWaiting(string message)
    {
        var nowUtc = CurrentUtcTime();

        if (_showOptionOpenInterest)
            SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot.Waiting(nowUtc, message));

        if (_showOptionPremiumFlow)
        {
            SetOptionFlowSnapshot(OptionFlowSnapshot.Waiting(
                nowUtc,
                message,
                _optionFlowBucketMode,
                _optionFlowTradeScope,
                _optionFlowIntervalMinutes));
        }
    }

    private void PublishOptionError(IbOptionGatewayException exception)
    {
        var state = exception.Code switch
        {
            "NO_PERMISSION" => OptionDataStatus.NoPermission,
            "LINE_LIMIT" => OptionDataStatus.LineLimit,
            "NO_CONTRACTS" => OptionDataStatus.NoContracts,
            _ => OptionDataStatus.Frozen
        };
        var nowUtc = CurrentUtcTime();

        if (_showOptionOpenInterest)
        {
            var current = Volatile.Read(ref _optionOpenInterestSnapshot);
            SetOptionOpenInterestSnapshot(current with
            {
                Status = state,
                Message = exception.Message
            });
        }

        if (_showOptionPremiumFlow)
        {
            var current = Volatile.Read(ref _optionFlowSnapshot);
            SetOptionFlowSnapshot(current with
            {
                Status = state,
                Message = exception.Message
            });
        }
    }

    private void SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot snapshot)
    {
        if (ReferenceEquals(Volatile.Read(ref _optionOpenInterestSnapshot), snapshot)) return;
        Volatile.Write(ref _optionOpenInterestSnapshot, snapshot);
        RequestRedraw();
    }

    private void SetOptionFlowSnapshot(OptionFlowSnapshot snapshot)
    {
        if (ReferenceEquals(Volatile.Read(ref _optionFlowSnapshot), snapshot)) return;
        Volatile.Write(ref _optionFlowSnapshot, snapshot);
        RequestRedraw();
    }
}
