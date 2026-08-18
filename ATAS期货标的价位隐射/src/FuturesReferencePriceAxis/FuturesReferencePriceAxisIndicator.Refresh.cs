namespace WolfMoss.ATAS.PriceMapping;

using System.Globalization;
using System.Net.Http;

using WolfMoss.ATAS.PriceMapping.Core;

public abstract partial class FuturesReferencePriceAxisIndicatorBase
{
    private void RestartSchedule()
    {
        CancellationToken cancellationToken;

        lock (_scheduleSync)
        {
            if (!_initialized || _lifetimeCancellation == null)
                return;

            _scheduleCancellation?.Cancel();
            _scheduleCancellation?.Dispose();
            _scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            cancellationToken = _scheduleCancellation.Token;
        }

        _ = RunRefreshLoopAsync(cancellationToken);
    }

    private void StopSchedule()
    {
        lock (_scheduleSync)
        {
            _scheduleCancellation?.Cancel();
            _scheduleCancellation?.Dispose();
            _scheduleCancellation = null;
        }
    }

    private bool HasActiveSchedule()
    {
        lock (_scheduleSync)
        {
            return _scheduleCancellation is { IsCancellationRequested: false };
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var consecutiveRetryFailures = 0;

            while (true)
            {
                var outcome = await RefreshOnceAsync(cancellationToken)
                    .ConfigureAwait(false);
                var delay = outcome == RefreshOutcome.RetrySoon
                    ? RetryDelayPolicy.ForConsecutiveFailure(consecutiveRetryFailures++)
                    : TimeSpan.FromMinutes(_refreshIntervalMinutes);

                if (outcome != RefreshOutcome.RetrySoon)
                    consecutiveRetryFailures = 0;

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetFailure($"自动更新循环异常: {ShortMessage(ex)}");
        }
    }

    private async Task<RefreshOutcome> RefreshOnceAsync(
        CancellationToken cancellationToken)
    {
        if (_mappingMode != Core.MappingMode.Automatic)
        {
            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Manual,
                CurrentUtcTime(),
                "使用手动比例"));
            return RefreshOutcome.Completed;
        }

        var enteredGate = false;
        var generation = -1L;

        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            generation = Interlocked.Read(ref _generation);

            if (!InstrumentPairResolver.TryResolve(
                    _pairMode,
                    InstrumentInfo?.Instrument,
                    out var pair))
            {
                SetFailure("无法识别期货标的，请手动选择映射组合");
                return RefreshOutcome.Completed;
            }

            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Fetching,
                CurrentUtcTime(),
                $"正在获取 {pair.ReferenceSymbol}"));

            var nowUtc = DateTime.UtcNow;
            var pending = Volatile.Read(ref _pendingReference);
            ReferenceMinuteClose reference;
            DateTime marketMinute;

            if (CanReusePendingReference(pending, generation, pair, nowUtc))
            {
                reference = pending!.Reference;
                marketMinute = pending.MarketMinute;
            }
            else
            {
                Volatile.Write(ref _pendingReference, null);
                var previous = Volatile.Read(ref _successfulMapping);
                reference = await YahooChartClient.GetLatestCompletedAsync(
                        pair.YahooSymbol,
                        nowUtc,
                        TimeSpan.FromMinutes(_maxQuoteAgeMinutes),
                        previous?.MinuteStartUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                marketMinute = ToMarketTime(reference.MinuteStartUtc);
                pending = new PendingReferenceSnapshot(
                    generation,
                    pair,
                    reference,
                    marketMinute,
                    nowUtc);
                Volatile.Write(ref _pendingReference, pending);
            }

            if (!IsCurrentGeneration(generation, cancellationToken))
                return RefreshOutcome.Completed;

            EnsureTradesCache();

            if (TryFindCachedClose(marketMinute, out var futuresClose))
            {
                ApplyMapping(generation, pair, reference, marketMinute, futuresClose);
                return RefreshOutcome.Completed;
            }

            var history = await RequestHistoricalTradesAsync(
                    marketMinute,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentGeneration(generation, cancellationToken))
                return RefreshOutcome.Completed;

            if (!MinuteCloseMatcher.TryFindClose(history, marketMinute, out futuresClose))
            {
                SetFailure($"ATAS 无 {marketMinute:yyyy-MM-dd HH:mm} 对应分钟成交");
                return RefreshOutcome.RetrySoon;
            }

            ApplyMapping(generation, pair, reference, marketMinute, futuresClose);
            return RefreshOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure("ATAS 历史成交请求超时，自动重试中");

            return RefreshOutcome.RetrySoon;
        }
        catch (AtasDataNotReadyException)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure("ATAS 数据连接未就绪，自动重试中");

            return RefreshOutcome.RetrySoon;
        }
        catch (AtasHistoricalRequestException)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure("ATAS 历史成交服务未就绪，自动重试中");

            return RefreshOutcome.RetrySoon;
        }
        catch (ReferenceDataException ex)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                var existing = Volatile.Read(ref _successfulMapping);
                var existingSymbol = existing?.Pair.YahooSymbol;
                var attemptUtc = CurrentUtcTime();
                var canRetainClosedSessionMapping = string.Equals(
                                                        existingSymbol,
                                                        "^GSPC",
                                                        StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(
                                                        existingSymbol,
                                                        "QQQ",
                                                        StringComparison.OrdinalIgnoreCase)
                                                    && (UsEquitySessionCalendar
                                                            .IsQqqExtendedSessionClosed(attemptUtc)
                                                        || !NqTradingSessionCalendar
                                                            .IsOpen(attemptUtc));

                if (ReferenceErrorPolicy.IsDuplicate(ex)
                    && canRetainClosedSessionMapping)
                {
                    var displaySymbol = string.Equals(
                        existingSymbol,
                        "QQQ",
                        StringComparison.OrdinalIgnoreCase)
                        ? "QQQ"
                        : "SPX";
                    SetAttempt(new UpdateAttemptSnapshot(
                        UpdateState.Success,
                        CurrentUtcTime(),
                        $"{displaySymbol} 无新K，沿用最近收盘映射"));
                    return RefreshOutcome.Completed;
                }

                SetFailure(ex.Message);
            }

            return ReferenceErrorPolicy.IsTransient(ex)
                ? RefreshOutcome.RetrySoon
                : RefreshOutcome.Completed;
        }
        catch (HttpRequestException ex)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure($"Yahoo 网络错误: {ShortMessage(ex)}");

            return RefreshOutcome.RetrySoon;
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure($"自动更新失败: {ShortMessage(ex)}");

            return RefreshOutcome.Completed;
        }
        finally
        {
            if (enteredGate)
                _refreshGate.Release();
        }
    }

    private bool CanReusePendingReference(
        PendingReferenceSnapshot? pending,
        long generation,
        InstrumentPair pair,
        DateTime nowUtc)
    {
        if (pending == null
            || pending.Generation != generation
            || pending.Pair != pair)
        {
            return false;
        }

        if (string.Equals(
                pair.YahooSymbol,
                "^GSPC",
                StringComparison.OrdinalIgnoreCase))
        {
            return nowUtc - pending.FetchedUtc
                   <= TimeSpan.FromMinutes(_refreshIntervalMinutes);
        }

        if (string.Equals(
                pair.YahooSymbol,
                "QQQ",
                StringComparison.OrdinalIgnoreCase)
            && (UsEquitySessionCalendar.IsQqqExtendedSessionClosed(nowUtc)
                || !NqTradingSessionCalendar.IsOpen(nowUtc)))
        {
            return nowUtc - pending.FetchedUtc
                   <= TimeSpan.FromMinutes(_refreshIntervalMinutes);
        }

        return nowUtc - pending.Reference.MinuteStartUtc.AddMinutes(1)
               <= TimeSpan.FromMinutes(_maxQuoteAgeMinutes);
    }

    private void ApplyMapping(
        long generation,
        InstrumentPair pair,
        ReferenceMinuteClose reference,
        DateTime marketMinute,
        decimal futuresClose)
    {
        if (generation != Interlocked.Read(ref _generation)
            || _mappingMode != Core.MappingMode.Automatic)
        {
            return;
        }

        if (!MappingMath.TryCalculateRatio(
                futuresClose,
                reference.Close,
                out var ratio))
        {
            SetFailure("同分钟比例无效");
            return;
        }

        var appliedUtc = CurrentUtcTime();
        var applied = new MappingSnapshot(
            pair,
            reference.MinuteStartUtc,
            marketMinute,
            futuresClose,
            reference.Close,
            ratio,
            appliedUtc);
        Volatile.Write(ref _successfulMapping, applied);
        Volatile.Write(ref _pendingReference, null);
        var nowUtc = appliedUtc;
        var isExpiredReference = nowUtc - reference.MinuteStartUtc.AddMinutes(1)
                                 > TimeSpan.FromMinutes(_maxQuoteAgeMinutes);
        var isLastSpxClose = string.Equals(
                                 pair.YahooSymbol,
                                 "^GSPC",
                                 StringComparison.OrdinalIgnoreCase)
                             && isExpiredReference;
        var isLastQqqClose = string.Equals(
                                 pair.YahooSymbol,
                                 "QQQ",
                                 StringComparison.OrdinalIgnoreCase)
                             && isExpiredReference
                             && (UsEquitySessionCalendar
                                     .IsQqqExtendedSessionClosed(nowUtc)
                                 || !NqTradingSessionCalendar.IsOpen(nowUtc));
        SetAttempt(new UpdateAttemptSnapshot(
            UpdateState.Success,
            applied.AppliedUtcTime,
            BuildSuccessMessage(
                reference.Source,
                isLastSpxClose,
                isLastQqqClose)));
    }

    private static string BuildSuccessMessage(
        string source,
        bool isLastSpxClose,
        bool isLastQqqClose)
    {
        var isNasdaq = string.Equals(
            source,
            "Nasdaq",
            StringComparison.OrdinalIgnoreCase);
        var isMarketWatch = string.Equals(
            source,
            "MarketWatch",
            StringComparison.OrdinalIgnoreCase);

        if (isLastSpxClose)
        {
            return isMarketWatch
                ? "SPX最近收盘映射成功（MW备用源）"
                : "SPX 最近收盘同分钟映射成功";
        }

        if (isLastQqqClose)
        {
            return isNasdaq
                ? "QQQ最近收盘映射成功（Nasdaq备用源）"
                : "QQQ 最近收盘同分钟映射成功";
        }

        if (isNasdaq)
            return "同分钟映射成功（Nasdaq备用源）";

        return isMarketWatch
            ? "同分钟映射成功（MW备用源）"
            : "同分钟映射更新成功";
    }

    private void SetFailure(string message)
    {
        var state = Volatile.Read(ref _successfulMapping) == null
            ? UpdateState.Failed
            : UpdateState.Frozen;
        SetAttempt(new UpdateAttemptSnapshot(
            state,
            CurrentUtcTime(),
            message));
    }

    private void SetAttempt(UpdateAttemptSnapshot snapshot)
    {
        Volatile.Write(ref _latestAttempt, snapshot);
        RequestRedraw();
    }

    private void ConfigurationChanged(bool restartSchedule)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _pendingReference, null);

        OnEditionConfigurationChanged();

        if (!_initialized)
            return;

        if (_mappingMode == Core.MappingMode.Manual)
        {
            StopSchedule();
            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Manual,
                CurrentUtcTime(),
                "使用手动比例"));
        }
        else if (restartSchedule || HasActiveSchedule())
        {
            RestartSchedule();
        }

        RequestRedraw();
    }

    private DateTime ToMarketTime(DateTime utcTime)
        => MarketTimeAlignment.UtcMinuteToMarketTime(
            utcTime,
            CurrentMarketTime(),
            CurrentUtcTime());

    private DateTime CurrentMarketTime()
    {
        try
        {
            return MarketTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    protected DateTime CurrentUtcTime()
    {
        try
        {
            return UtcTime;
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private bool IsCurrentGeneration(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return generation == Interlocked.Read(ref _generation);
    }

    protected void RequestRedraw()
    {
        if (!_initialized || DataProvider == null)
            return;

        try
        {
            DataProvider.DoActionInGuiThread(() => RedrawChart());
        }
        catch
        {
        }
    }

    protected static string ShortMessage(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 100 ? message : message[..100];
    }

}
