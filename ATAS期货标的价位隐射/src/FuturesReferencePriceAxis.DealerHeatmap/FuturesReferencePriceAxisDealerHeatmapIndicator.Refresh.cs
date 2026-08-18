namespace WolfMoss.ATAS.PriceMapping;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private readonly object _dealerHeatmapScheduleSync = new();
    private CancellationTokenSource? _dealerHeatmapLifetimeCancellation;
    private CancellationTokenSource? _dealerHeatmapScheduleCancellation;
    private DealerHeatmapSnapshot _dealerHeatmapSnapshot =
        DealerHeatmapSnapshot.Disabled(DateTime.MinValue);
    private long _dealerHeatmapGeneration;

    protected override void OnEditionInitialized()
    {
        _dealerHeatmapLifetimeCancellation = new CancellationTokenSource();
        RestartDealerHeatmapSchedule();
    }

    protected override void OnEditionFinishRecalculate()
        => RestartDealerHeatmapSchedule();

    protected override void OnEditionDataProviderChanged()
    {
        if (DataProvider == null)
        {
            StopDealerHeatmapSchedule();
            return;
        }

        RestartDealerHeatmapSchedule();
    }

    protected override void OnEditionConfigurationChanged()
    {
        Interlocked.Increment(ref _dealerHeatmapGeneration);

        if (IsIndicatorInitialized)
            RestartDealerHeatmapSchedule();
    }

    protected override void OnEditionDisposing()
    {
        Interlocked.Increment(ref _dealerHeatmapGeneration);
        StopDealerHeatmapSchedule();
        _dealerHeatmapLifetimeCancellation?.Cancel();
        _dealerHeatmapLifetimeCancellation?.Dispose();
        _dealerHeatmapLifetimeCancellation = null;
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

            if (!_showDealerHeatmap)
            {
                SetDealerHeatmapSnapshot(
                    DealerHeatmapSnapshot.Disabled(CurrentUtcTime()));
                return;
            }

            if (string.IsNullOrWhiteSpace(_nightwatchApiKey))
            {
                SetDealerHeatmapSnapshot(
                    DealerHeatmapSnapshot.MissingApiKey(CurrentUtcTime()));
                return;
            }

            _dealerHeatmapScheduleCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _dealerHeatmapLifetimeCancellation.Token);
            cancellationToken = _dealerHeatmapScheduleCancellation.Token;
        }

        var generation = Interlocked.Read(ref _dealerHeatmapGeneration);
        _ = RunDealerHeatmapLoopAsync(generation, cancellationToken);
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

    private async Task RunDealerHeatmapLoopAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var retryNotBefore = await RefreshDealerHeatmapOnceAsync(
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
                var snapshot = Volatile.Read(ref _dealerHeatmapSnapshot);
                SetDealerHeatmapSnapshot(snapshot with { NextAttemptUtc = nextUtc });
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
            var existing = Volatile.Read(ref _dealerHeatmapSnapshot);
            SetDealerHeatmapSnapshot(existing with
            {
                State = DealerHeatmapState.Frozen,
                AttemptUtc = CurrentUtcTime(),
                Message = "Dealer Heatmap 更新循环异常",
                IsFrozen = existing.Frame != null
            });
        }
    }

    private async Task<DateTime?> RefreshDealerHeatmapOnceAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        var nowUtc = CurrentUtcTime();

        if (!InstrumentPairResolver.TryResolve(
                ConfiguredPairMode,
                InstrumentInfo?.Instrument,
                out var pair))
        {
            SetDealerHeatmapSnapshot(new DealerHeatmapSnapshot(
                null,
                null,
                DealerHeatmapState.Waiting,
                nowUtc,
                null,
                "等待可识别的 NQ/MNQ 或 ES/MES 品种",
                false));
            return null;
        }

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

            var isLive = IsLiveFrame(target, frame);
            var state = isLive
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
}
