namespace WolfMoss.ATAS.PriceMapping;

using global::ATAS.Indicators;

using WolfMoss.ATAS.PriceMapping.Core;

public sealed partial class FuturesReferencePriceAxisIndicator
{
    protected override void OnInitialize()
    {
        base.OnInitialize();

        _initialized = true;
        _lifetimeCancellation = new CancellationTokenSource();

        EnsureTradesCache();
        SetAttempt(UpdateAttemptSnapshot.Waiting(
            CurrentUtcTime(),
            "等待图表历史数据就绪"));
        _ = StartInitializationFallbackAsync(_lifetimeCancellation.Token);
    }

    protected override void OnFinishRecalculate()
    {
        base.OnFinishRecalculate();

        if (!_initialized)
            return;

        EnsureTradesCache();
        RestartSchedule();
    }

    protected override void OnDataProviderChanged(
        IIndicatorDataProvider? oldDataProvider,
        IIndicatorDataProvider? newDataProvider)
    {
        base.OnDataProviderChanged(oldDataProvider, newDataProvider);

        if (!_initialized)
            return;

        StopSchedule();
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _pendingReference, null);
        _tradesCache = null;
        CancelPendingHistoryRequests();

        if (newDataProvider == null)
        {
            SetFailure("等待 ATAS 数据连接，连接后自动重试");
            return;
        }

        EnsureTradesCache();
        RestartSchedule();
    }

    private async Task StartInitializationFallbackAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            if (_initialized
                && _mappingMode == Core.MappingMode.Automatic
                && DataProvider != null
                && !HasActiveSchedule())
            {
                RestartSchedule();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    protected override void OnDispose()
    {
        _initialized = false;
        Interlocked.Increment(ref _generation);

        StopSchedule();

        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;

        Volatile.Write(ref _pendingReference, null);
        CancelPendingHistoryRequests();
        base.OnDispose();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1)
            return;

        var candle = GetCandle(bar);
        var close = candle?.Close ?? 0m;

        if (close <= 0m)
            return;

        lock (_priceSync)
            _latestChartPrice = close;
    }

    protected override void OnCumulativeTradesResponse(
        CumulativeTradesRequest request,
        IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        base.OnCumulativeTradesResponse(request, cumulativeTrades);

        if (!_historyRequests.TryRemove(request.RequestId, out var completion))
            return;

        try
        {
            var samples = cumulativeTrades
                .Where(x => x?.Ticks != null)
                .SelectMany(x => x.Ticks)
                .Where(x => x != null && x.Price > 0m)
                .Select(x => new MarketTradeSample(x.Time, x.Price))
                .ToArray();
            completion.TrySetResult(samples);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private void EnsureTradesCache()
    {
        if (_tradesCache != null || DataProvider == null)
            return;

        try
        {
            _tradesCache = GetTradesCache(TimeSpan.FromHours(2));
        }
        catch
        {
            _tradesCache = null;
        }
    }

    private void CancelPendingHistoryRequests()
    {
        foreach (var request in _historyRequests.Values)
            request.TrySetCanceled();

        _historyRequests.Clear();
    }

    private bool TryFindCachedClose(DateTime marketMinute, out decimal close)
    {
        close = 0m;

        try
        {
            var cachedItems = _tradesCache?.CachedItems;

            if (cachedItems == null)
                return false;

            var samples = cachedItems
                .Where(x => x != null && x.Price > 0m)
                .Select(x => new MarketTradeSample(x.Time, x.Price))
                .ToArray();
            return MinuteCloseMatcher.TryFindClose(samples, marketMinute, out close);
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<MarketTradeSample>> RequestHistoricalTradesAsync(
        DateTime marketMinute,
        CancellationToken cancellationToken)
    {
        var dataProvider = DataProvider
                           ?? throw new AtasDataNotReadyException();
        var request = new CumulativeTradesRequest(
            marketMinute.AddSeconds(-1),
            marketMinute.AddMinutes(1).AddSeconds(1),
            0,
            0);
        var completion =
            new TaskCompletionSource<IReadOnlyList<MarketTradeSample>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_historyRequests.TryAdd(request.RequestId, completion))
            throw new InvalidOperationException("历史成交请求编号冲突");

        void SubmitRequest()
        {
            try
            {
                RequestForCumulativeTrades(request);
            }
            catch (Exception ex)
            {
                if (_historyRequests.TryRemove(request.RequestId, out var pending))
                {
                    pending.TrySetException(
                        new AtasHistoricalRequestException(ex));
                }
            }
        }

        try
        {
            try
            {
                dataProvider.DoActionInGuiThread(SubmitRequest);
            }
            catch (Exception ex)
            {
                throw new AtasHistoricalRequestException(ex);
            }

            return await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _historyRequests.TryRemove(request.RequestId, out _);
        }
    }

}
