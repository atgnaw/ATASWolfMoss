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
    private CancellationTokenSource? _optionLifetimeCancellation;
    private CancellationTokenSource? _optionScheduleCancellation;
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

    private void RestartOptionDataSchedule()
    {
        CancellationToken cancellationToken;

        lock (_optionScheduleSync)
        {
            if (!IsIndicatorInitialized || _optionLifetimeCancellation == null)
                return;

            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation?.Dispose();
            _optionScheduleCancellation = null;
            _optionSubscription?.Dispose();
            _optionSubscription = null;
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
                _ = ReleaseOptionGatewayAsync();
                return;
            }

            _optionScheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _optionLifetimeCancellation.Token);
            cancellationToken = _optionScheduleCancellation.Token;
        }

        var generation = Interlocked.Read(ref _optionDataGeneration);
        _ = RunOptionDataLoopAsync(generation, cancellationToken);
    }

    private void PauseOptionDataSchedule()
    {
        Interlocked.Increment(ref _optionDataGeneration);

        lock (_optionScheduleSync)
        {
            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation?.Dispose();
            _optionScheduleCancellation = null;
            _optionSubscription?.Dispose();
            _optionSubscription = null;
        }
    }

    private void StopOptionData()
    {
        Interlocked.Increment(ref _optionDataGeneration);

        lock (_optionScheduleSync)
        {
            _optionScheduleCancellation?.Cancel();
            _optionScheduleCancellation?.Dispose();
            _optionScheduleCancellation = null;
            _optionSubscription?.Dispose();
            _optionSubscription = null;
        }

        _optionLifetimeCancellation?.Cancel();
        _optionLifetimeCancellation?.Dispose();
        _optionLifetimeCancellation = null;
        _ = ReleaseOptionGatewayAsync();
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
        var retryIndex = 0;
        var retryDelays = new[] { 2, 5, 15, 30, 60 };

        while (!cancellationToken.IsCancellationRequested
               && generation == Interlocked.Read(ref _optionDataGeneration))
        {
            try
            {
                await EnsureOptionTargetAsync(generation, cancellationToken)
                    .ConfigureAwait(false);
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
                PublishOptionError(exception);
                ReleaseOptionGatewayAfterFailure(exception.Code);
                var seconds = retryDelays[Math.Min(retryIndex++, retryDelays.Length - 1)];
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _ = ReleaseOptionGatewayAsync();
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

        var ratio = CurrentEffectiveMappingRatio;
        var futuresPrice = LatestChartPrice;

        if (ratio is not > 0m || futuresPrice <= 0m)
        {
            SetOptionWaiting("等待有效映射比例与期货现价");
            return;
        }

        var nowUtc = CurrentUtcTime();
        var expiration = NyseTradingCalendar
            .ResolveTarget(nowUtc, profile.Ticker)
            .TargetExpiration;
        var gateway = GetOrCreateOptionGateway();
        await gateway.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var available = await gateway.GetAvailableStrikesAsync(
                profile, expiration, cancellationToken)
            .ConfigureAwait(false);
        var mappedSpot = futuresPrice / ratio.Value;
        var selected = OptionStrikeSelection.SelectCentered(
            available,
            mappedSpot,
            _optionStrikeLevels,
            out var candidateAtm);
        selected = OptionStrikeSelection.ApplySymmetricBudget(
            selected,
            candidateAtm,
            _ibOptionMarketDataLineBudget);

        if (selected.Count == 0)
            throw new IbOptionGatewayException("NO_CONTRACTS", "ATM 附近没有可用 0DTE 执行价");

        var targetChanged = !string.Equals(_activeOptionTicker, profile.Ticker,
                                StringComparison.OrdinalIgnoreCase)
                            || _activeOptionExpiration != expiration;
        var atmChanged = candidateAtm != _activeOptionSelectionAtmStrike;

        if (!targetChanged && atmChanged && _activeOptionSelectionAtmStrike > 0m)
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
            await MaintainOptionSubscriptionAsync(cancellationToken).ConfigureAwait(false);
            AdvanceFixedFlowBucketLock(nowUtc);

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
        _optionSubscription?.Dispose();
        _optionSubscription = null;
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
                _regularTradeSamples.Clear();
                _allTradeSamples.Clear();
                _flowCoverageStartUtc = configuredUtc;
                _optionOpenInterest.Clear();
                _optionOpenInterestReceivedUtc = DateTime.MinValue;
                var cached = OptionOpenInterestCache.LoadSnapshot(
                    OptionOpenInterestCache.GetDefaultDirectory(),
                    profile.Ticker,
                    expiration);

                foreach (var item in cached.Values)
                {
                    _optionOpenInterest[item.Key] = item.Value;
                }

                if (cached.Values.Count > 0)
                    _optionOpenInterestReceivedUtc = cached.ReceivedUtc;
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
        }

        await MaintainOptionSubscriptionAsync(cancellationToken).ConfigureAwait(false);

        if (_showOptionOpenInterest && _optionSubscription == null)
            await FetchOpenInterestInBatchesAsync(contracts, cancellationToken)
                .ConfigureAwait(false);

        ScheduleNextOiRetry(CurrentUtcTime());
    }

    private async Task FetchOpenInterestInBatchesAsync(
        IReadOnlyList<OptionContractDescriptor> contracts,
        CancellationToken cancellationToken)
    {
        var gateway = GetOrCreateOptionGateway();
        var pending = contracts.Where(contract =>
        {
            lock (_optionDataSync)
                return !_optionOpenInterest.ContainsKey(contract.ConId);
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
                OnOptionMarketData(update);

                if (update.OpenInterest.HasValue)
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
    {
        if (!string.IsNullOrWhiteSpace(update.ErrorCode))
        {
            PublishOptionError(new IbOptionGatewayException(
                update.ErrorCode,
                update.ErrorMessage ?? "IB 期权行情订阅失败"));
            return;
        }

        lock (_optionDataSync)
        {
            if (!string.Equals(update.Contract.Ticker, _activeOptionTicker,
                    StringComparison.OrdinalIgnoreCase)
                || update.Contract.Expiration != _activeOptionExpiration)
            {
                return;
            }

            _optionDataIsDelayed |= update.IsDelayed;

            if (update.OpenInterest.HasValue && update.OpenInterest.Value >= 0)
            {
                _optionOpenInterest[update.Contract.ConId] = update.OpenInterest.Value;

                if (update.ReceivedUtc > _optionOpenInterestReceivedUtc)
                    _optionOpenInterestReceivedUtc = update.ReceivedUtc;

                _oiCacheDirty = true;
            }

            if (update.RegularTrades.HasValue)
                AddCumulativeSample(_regularTradeSamples, update.RegularTrades.Value);

            if (update.AllTimeAndSales.HasValue)
                AddCumulativeSample(_allTradeSamples, update.AllTimeAndSales.Value);
        }

        RequestRedraw();
    }

    private static void AddCumulativeSample(
        Dictionary<long, List<OptionCumulativeSample>> destination,
        OptionCumulativeSample sample)
    {
        if (!destination.TryGetValue(sample.ConId, out var samples))
        {
            samples = new List<OptionCumulativeSample>();
            destination.Add(sample.ConId, samples);
        }

        if (samples.Count > 0
            && (sample.SampleUtc < samples[^1].SampleUtc
                || sample.TotalVolume < samples[^1].TotalVolume))
        {
            samples.Clear();
        }

        if (samples.Count > 0 && samples[^1].SampleUtc == sample.SampleUtc)
            samples[^1] = sample;
        else
            samples.Add(sample);

        var cutoff = sample.SampleUtc - TimeSpan.FromHours(2);
        samples.RemoveAll(value => value.SampleUtc < cutoff);
    }

    private void PublishOptionSnapshots()
    {
        IReadOnlyList<OptionContractDescriptor> contracts;
        IReadOnlyList<decimal> strikes;
        Dictionary<long, long> oi;
        Dictionary<long, List<OptionCumulativeSample>> samples;
        string? ticker;
        DateOnly? expiration;
        decimal atm;
        DateTime coverageStart;
        DateTime oiReceivedUtc;
        bool delayed;

        lock (_optionDataSync)
        {
            contracts = _activeOptionContracts.ToArray();
            strikes = _activeOptionStrikes.ToArray();
            oi = new Dictionary<long, long>(_optionOpenInterest);
            var source = _optionFlowTradeScope == OptionFlowTradeScope.RegularTrades
                ? _regularTradeSamples
                : _allTradeSamples;
            samples = source.ToDictionary(
                static item => item.Key,
                static item => item.Value.ToList());
            ticker = _activeOptionTicker;
            expiration = _activeOptionExpiration;
            atm = _activeAtmStrike;
            coverageStart = _flowCoverageStartUtc;
            oiReceivedUtc = _optionOpenInterestReceivedUtc;
            delayed = _optionDataIsDelayed;
        }

        if (ticker == null || !expiration.HasValue || strikes.Count == 0)
            return;

        var nowUtc = CurrentUtcTime();

        if (_showOptionOpenInterest)
        {
            var oiRows = CreateRows(strikes, contracts, oi, null);
            var populated = contracts.Count(contract => oi.ContainsKey(contract.ConId));
            var coverage = OptionContractCoverage.Calculate(strikes, contracts);
            var status = populated == 0
                ? OptionDataStatus.WaitingOpenInterest
                : populated == contracts.Count && coverage.IsComplete
                    ? OptionDataStatus.Daily
                    : OptionDataStatus.Partial;
            SetOptionOpenInterestSnapshot(new OptionOpenInterestSnapshot(
                ticker,
                expiration,
                atm,
                oiRows,
                status,
                populated > 0 ? oiReceivedUtc : DateTime.MinValue,
                $"OI {populated}/{contracts.Count}；合约 {coverage.ResolvedContractCount}/{coverage.ExpectedContractCount}",
                _optionStrikeLevels,
                coverage.ActiveStrikeCount));
        }

        if (_showOptionPremiumFlow)
        {
            SetOptionFlowSnapshot(CreateFlowSnapshot(
                ticker,
                expiration.Value,
                atm,
                strikes,
                contracts,
                samples,
                nowUtc,
                coverageStart,
                delayed));
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
        var segments = contracts.Count == 0
            ? Array.Empty<OptionTradingSegment>()
            : IbTradingHoursParser.Parse(
                    contracts[0].TradingHours,
                    contracts[0].TradingTimeZoneId,
                    string.Equals(ticker, "SPX", StringComparison.OrdinalIgnoreCase))
                .ToArray();
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
                    return current;
                }

                startUtc = bucket.Value.StartUtc;
                endUtc = bucket.Value.EndUtc;
                PopulateIntervalValues(
                    samples,
                    startUtc.Value,
                    endUtc.Value,
                    allowPartial: true,
                    values);
            }
        }
        else if (_optionFlowBucketMode == OptionFlowBucketMode.Rolling)
        {
            startUtc = nowUtc.AddMinutes(-_optionFlowIntervalMinutes);
            endUtc = nowUtc;

            if (coverageStartUtc <= startUtc)
            {
                PopulateIntervalValues(
                    samples,
                    startUtc.Value,
                    endUtc.Value,
                    allowPartial: false,
                    values);
            }
        }

        var rows = CreateRows(strikes, contracts, null, values);
        var isOpen = segments.Any(value => value.Contains(nowUtc));
        var isInitialPartialBucket = startUtc.HasValue
                                     && coverageStartUtc > startUtc.Value
                                     && values.Count == 0;
        var status = delayed
            ? OptionDataStatus.Delayed
            : !startUtc.HasValue || !endUtc.HasValue || isInitialPartialBucket
                ? OptionDataStatus.Warming
                : isOpen
                    ? OptionDataStatus.Live
                    : OptionDataStatus.Closed;
        var partialCount = values.Count(static item => item.Value.IsPartial);
        var message = status == OptionDataStatus.Warming
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
        Dictionary<long, OptionIntervalValue> destination)
    {
        foreach (var item in samples)
        {
            if (OptionFlowAggregation.TryCalculateBucketValue(
                    item.Value,
                    startUtc,
                    endUtc,
                    allowPartial,
                    out var value))
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
                !_optionOpenInterest.ContainsKey(contract.ConId));
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
    {
        var eastern = UsMarketClock.ToEastern(nowUtc);
        var date = eastern.Date;
        var candidates = new[]
        {
            date.AddHours(8).AddMinutes(30),
            date.AddHours(9).AddMinutes(15),
            date.AddHours(9).AddMinutes(35)
        };

        foreach (var candidate in candidates)
        {
            if (candidate > eastern)
                return NyseTradingCalendar.EasternToUtc(candidate);
        }

        return DateTime.MaxValue;
    }

    private void SaveOpenInterestCacheIfNeeded()
    {
        string? ticker;
        DateOnly? expiration;
        Dictionary<long, long> values;
        DateTime receivedUtc;

        lock (_optionDataSync)
        {
            if (!_oiCacheDirty || _activeOptionTicker == null || !_activeOptionExpiration.HasValue)
                return;

            _oiCacheDirty = false;
            ticker = _activeOptionTicker;
            expiration = _activeOptionExpiration;
            values = new Dictionary<long, long>(_optionOpenInterest);
            receivedUtc = _optionOpenInterestReceivedUtc;
        }

        try
        {
            OptionOpenInterestCache.Save(
                OptionOpenInterestCache.GetDefaultDirectory(),
                ticker,
                expiration.Value,
                receivedUtc == DateTime.MinValue ? CurrentUtcTime() : receivedUtc,
                values);
        }
        catch
        {
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
                ReceivedUtc = nowUtc,
                Message = exception.Message
            });
        }

        if (_showOptionPremiumFlow)
        {
            var current = Volatile.Read(ref _optionFlowSnapshot);
            SetOptionFlowSnapshot(current with
            {
                Status = state,
                ReceivedUtc = nowUtc,
                Message = exception.Message
            });
        }
    }

    private void SetOptionOpenInterestSnapshot(OptionOpenInterestSnapshot snapshot)
    {
        Volatile.Write(ref _optionOpenInterestSnapshot, snapshot);
        RequestRedraw();
    }

    private void SetOptionFlowSnapshot(OptionFlowSnapshot snapshot)
    {
        Volatile.Write(ref _optionFlowSnapshot, snapshot);
        RequestRedraw();
    }
}
