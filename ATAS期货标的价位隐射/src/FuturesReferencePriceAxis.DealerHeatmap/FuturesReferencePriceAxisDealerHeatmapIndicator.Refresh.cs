namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly object _dealerHeatmapScheduleSync = new();
    private CancellationTokenSource? _dealerHeatmapLifetimeCancellation;
    private CancellationTokenSource? _dealerHeatmapScheduleCancellation;
    private DealerHeatmapSnapshot _dealerHeatmapSnapshot =
        DealerHeatmapSnapshot.Disabled(DateTime.MinValue);
    private DealerGexSnapshot _dealerGexSnapshot =
        DealerGexSnapshot.Disabled(DateTime.MinValue);
    private long _dealerHeatmapGeneration;

    protected override void OnEditionInitialized()
    {
        foreach (var source in RegisteredLifecycles) source.Initialize(this);
        RestartPerformanceDiagnostics();
    }

    protected override void OnEditionFinishRecalculate()
    {
        foreach (var source in RegisteredLifecycles) source.Restart(this);
    }

    protected override void OnEditionDataProviderChanged()
    {
        if (DataProvider == null)
        {
            foreach (var source in RegisteredLifecycles) source.Pause(this);
            return;
        }

        foreach (var source in RegisteredLifecycles) source.Restart(this);
    }

    protected override void OnEditionConfigurationChanged()
    {
        foreach (var source in RegisteredLifecycles) source.Changed(this);
    }

    protected override void OnEditionDisposing()
    {
        StopPerformanceDiagnostics();
        foreach (var source in RegisteredLifecycles) source.Stop(this);
    }

    private void RestartDealerHeatmapSchedule()
    {
        CancellationToken cancellationToken;

        lock (_dealerHeatmapScheduleSync)
        {
            if (!IsIndicatorInitialized || _dealerHeatmapLifetimeCancellation == null)
                return;

            _dealerHeatmapScheduleCancellation?.Cancel();
            _dealerHeatmapScheduleCancellation?.Dispose();
            _dealerHeatmapScheduleCancellation = null;
            var nowUtc = CurrentUtcTime();

            if (!_showDealerHeatmap)
                SetDealerHeatmapSnapshot(DealerHeatmapSnapshot.Disabled(nowUtc));

            if (!_showDealerGex)
                SetDealerGexSnapshot(DealerGexSnapshot.Disabled(nowUtc));

            if (!_showDealerHeatmap && !_showDealerGex)
                return;

            if (string.IsNullOrWhiteSpace(_nightwatchApiKey))
            {
                if (_showDealerHeatmap)
                    SetDealerHeatmapSnapshot(DealerHeatmapSnapshot.MissingApiKey(nowUtc));

                if (_showDealerGex)
                    SetDealerGexSnapshot(DealerGexSnapshot.MissingApiKey(nowUtc));

                return;
            }

            _dealerHeatmapScheduleCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _dealerHeatmapLifetimeCancellation.Token);
            cancellationToken = _dealerHeatmapScheduleCancellation.Token;
        }

        var generation = Interlocked.Read(ref _dealerHeatmapGeneration);
        _ = RunDealerDataLoopAsync(generation, cancellationToken);
    }

    private void StopDealerHeatmapSchedule()
    {
        lock (_dealerHeatmapScheduleSync)
        {
            _dealerHeatmapScheduleCancellation?.Cancel();
            _dealerHeatmapScheduleCancellation?.Dispose();
            _dealerHeatmapScheduleCancellation = null;
        }
    }

    private async Task RunDealerDataLoopAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        using var diagnosticTask = _performance.TrackTask();
        try
        {
            while (true)
            {
                var retryNotBefore = await RefreshDealerDataOnceAsync(
                        generation,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!IsCurrentDealerHeatmapGeneration(generation, cancellationToken))
                    return;

                var nowUtc = CurrentUtcTime();
                var nextUtc = DealerHeatmapSchedule.CalculateNextAttemptUtc(
                    nowUtc,
                    _dealerHeatmapRthRefreshMinutes,
                    _dealerHeatmapOffHoursRefreshMinutes,
                    retryNotBefore);

                if (_showDealerHeatmap)
                {
                    var heatmap = Volatile.Read(ref _dealerHeatmapSnapshot);
                    SetDealerHeatmapSnapshot(heatmap with { NextAttemptUtc = nextUtc });
                }

                if (_showDealerGex)
                {
                    var dealerGex = Volatile.Read(ref _dealerGexSnapshot);
                    SetDealerGexSnapshot(dealerGex with { NextAttemptUtc = nextUtc });
                }

                var delay = nextUtc - CurrentUtcTime();

                if (delay < TimeSpan.FromSeconds(1))
                    delay = TimeSpan.FromSeconds(1);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            var nowUtc = CurrentUtcTime();

            if (_showDealerHeatmap)
            {
                var existing = Volatile.Read(ref _dealerHeatmapSnapshot);
                SetDealerHeatmapSnapshot(existing with
                {
                    State = DealerHeatmapState.Frozen,
                    AttemptUtc = nowUtc,
                    Message = "Dealer Heatmap 更新循环异常",
                    IsFrozen = existing.Frame != null
                });
            }

            if (_showDealerGex)
            {
                var existing = Volatile.Read(ref _dealerGexSnapshot);
                SetDealerGexSnapshot(existing with
                {
                    State = DealerGexState.Frozen,
                    AttemptUtc = nowUtc,
                    Message = "Dealer GEX 更新循环异常",
                    IsFrozen = existing.Frame != null
                });
            }
        }
    }

    private async Task<DateTime?> RefreshDealerDataOnceAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        var nowUtc = CurrentUtcTime();

        if (!InstrumentPairResolver.TryResolve(
                ConfiguredPairMode,
                InstrumentInfo?.Instrument,
                out var pair))
        {
            if (_showDealerHeatmap)
            {
                SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                    null,
                    null,
                    DealerHeatmapState.Waiting,
                    nowUtc,
                    null,
                    "等待可识别的 NQ/MNQ 或 ES/MES 品种",
                    false));
            }

            if (_showDealerGex)
            {
                SetDealerGexSnapshot(new DealerGexSnapshot(
                    null,
                    null,
                    DealerGexState.Waiting,
                    nowUtc,
                    null,
                    "等待可识别的 NQ/MNQ 或 ES/MES 品种",
                    false));
            }

            return null;
        }

        var heatmapTask = _showDealerHeatmap
            ? RefreshDealerHeatmapOnceAsync(pair, generation, cancellationToken)
            : Task.FromResult<DateTime?>(null);
        var dealerGexTask = _showDealerGex
            ? RefreshDealerGexOnceAsync(pair, generation, cancellationToken)
            : Task.FromResult<DateTime?>(null);
        await Task.WhenAll(heatmapTask, dealerGexTask).ConfigureAwait(false);
        var retryDates = new[] { heatmapTask.Result, dealerGexTask.Result }
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        return retryDates.Length == 0 ? null : retryDates.Max();
    }

    private async Task<DateTime?> RefreshDealerHeatmapOnceAsync(
        InstrumentPair pair,
        long generation,
        CancellationToken cancellationToken)
    {
        var nowUtc = CurrentUtcTime();
        var target = NyseTradingCalendar.ResolveTarget(nowUtc, pair.ReferenceSymbol);
        var previous = Volatile.Read(ref _dealerHeatmapSnapshot);
        var reusableFrame = IsFrameForTarget(previous.Frame, target)
            ? previous.Frame
            : null;
        SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
            target,
            reusableFrame,
            DealerHeatmapState.Fetching,
            nowUtc,
            null,
            "正在获取 Dealer Heatmap",
            reusableFrame != null && previous.IsFrozen));

        try
        {
            var frame = await NightwatchDealerHeatmapClient.GetLatestAsync(
                    _nightwatchApiKey,
                    target.Ticker,
                    target.TargetExpiration,
                    nowUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentDealerHeatmapGeneration(generation, cancellationToken))
                return null;

            var state = IsLiveFrame(target, frame)
                ? DealerHeatmapState.Live
                : DealerHeatmapState.NextSession;
            var message = previous.Frame?.MinuteAtUtc == frame.MinuteAtUtc
                ? $"数据未推进，沿用 {frame.Cells.Count} 个节点"
                : $"已更新 {frame.Cells.Count} 个节点";
            SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                target,
                frame,
                state,
                CurrentUtcTime(),
                null,
                message,
                false));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DealerHeatmapDataException exception)
        {
            if (!IsCurrentDealerHeatmapGeneration(generation, cancellationToken))
                return exception.RetryAfterUtc;

            var state = exception.Code switch
            {
                "HTTP401" or "HTTP403" => DealerHeatmapState.AuthenticationFailed,
                "HTTP429" => DealerHeatmapState.RateLimited,
                _ when reusableFrame != null => DealerHeatmapState.Frozen,
                _ => DealerHeatmapState.Waiting
            };
            SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                target,
                reusableFrame,
                state,
                CurrentUtcTime(),
                exception.RetryAfterUtc,
                exception.Message,
                reusableFrame != null));
            return exception.RetryAfterUtc;
        }
        catch
        {
            SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                target,
                reusableFrame,
                reusableFrame != null
                    ? DealerHeatmapState.Frozen
                    : DealerHeatmapState.Waiting,
                CurrentUtcTime(),
                null,
                "Dealer Heatmap 内部错误",
                reusableFrame != null));
            return null;
        }
    }

    private async Task<DateTime?> RefreshDealerGexOnceAsync(
        InstrumentPair pair,
        long generation,
        CancellationToken cancellationToken)
    {
        var nowUtc = CurrentUtcTime();
        var ticker = pair.ReferenceSymbol.ToUpperInvariant();
        var previous = Volatile.Read(ref _dealerGexSnapshot);
        var reusableFrame = IsDealerGexFrameForTicker(previous.Frame, ticker)
            ? previous.Frame
            : null;
        SetDealerGexSnapshot(new DealerGexSnapshot(
            ticker,
            reusableFrame,
            DealerGexState.Fetching,
            nowUtc,
            null,
            "正在获取 Dealer GEX",
            reusableFrame != null && previous.IsFrozen));

        try
        {
            var frame = await NightwatchDealerGexClient.GetLatestAsync(
                    _nightwatchApiKey,
                    ticker,
                    nowUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentDealerHeatmapGeneration(generation, cancellationToken))
                return null;

            var state = DealerGexSessionPolicy.ResolveState(CurrentUtcTime(), frame);
            var message = previous.Frame?.SnapshotAtUtc == frame.SnapshotAtUtc
                ? $"数据未推进，沿用 {frame.Nodes.Count} 个关键价位"
                : $"已更新 {frame.Nodes.Count} 个关键价位";

            if (state == DealerGexState.Frozen)
                message += $"；API state: {frame.ApiState}";

            SetDealerGexSnapshot(new DealerGexSnapshot(
                ticker,
                frame,
                state,
                CurrentUtcTime(),
                null,
                message,
                state == DealerGexState.Frozen));
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DealerGexDataException exception)
        {
            if (!IsCurrentDealerHeatmapGeneration(generation, cancellationToken))
                return exception.RetryAfterUtc;

            var state = exception.Code switch
            {
                "HTTP401" or "HTTP403" => DealerGexState.AuthenticationFailed,
                "HTTP429" => DealerGexState.RateLimited,
                _ when reusableFrame != null => DealerGexState.Frozen,
                _ => DealerGexState.Waiting
            };
            SetDealerGexSnapshot(new DealerGexSnapshot(
                ticker,
                reusableFrame,
                state,
                CurrentUtcTime(),
                exception.RetryAfterUtc,
                exception.Message,
                reusableFrame != null));
            return exception.RetryAfterUtc;
        }
        catch
        {
            SetDealerGexSnapshot(new DealerGexSnapshot(
                ticker,
                reusableFrame,
                reusableFrame != null
                    ? DealerGexState.Frozen
                    : DealerGexState.Waiting,
                CurrentUtcTime(),
                null,
                "Dealer GEX 内部错误",
                reusableFrame != null));
            return null;
        }
    }

    private bool IsCurrentDealerHeatmapGeneration(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return generation == Interlocked.Read(ref _dealerHeatmapGeneration);
    }

    private static bool IsFrameForTarget(
        DealerHeatmapFrame? frame,
        DealerHeatmapTarget target)
        => frame != null
           && string.Equals(frame.Ticker, target.Ticker, StringComparison.OrdinalIgnoreCase)
           && frame.Expiration == target.TargetExpiration;

    private static bool IsDealerGexFrameForTicker(
        DealerGexFrame? frame,
        string ticker)
        => frame != null
           && string.Equals(frame.Ticker, ticker, StringComparison.OrdinalIgnoreCase);

    private static bool IsLiveFrame(
        DealerHeatmapTarget target,
        DealerHeatmapFrame frame)
    {
        if (target.SessionState != DealerHeatmapSessionState.RegularTradingHours)
            return false;

        var eastern = UsMarketClock.ToEastern(frame.MinuteAtUtc);
        return DateOnly.FromDateTime(eastern) == target.TargetExpiration
               && eastern.TimeOfDay >= NyseTradingCalendar.RegularOpen
               && eastern.TimeOfDay <= NyseTradingCalendar.GetRegularClose(eastern.Date);
    }

    private void SetDealerHeatmapSnapshot(DealerHeatmapSnapshot snapshot)
    {
        Volatile.Write(ref _dealerHeatmapSnapshot, snapshot);
        RequestRedraw();
    }

    private void SetDealerGexSnapshot(DealerGexSnapshot snapshot)
    {
        Volatile.Write(ref _dealerGexSnapshot, snapshot);
        RequestRedraw();
    }
}
