namespace WolfMoss.ATAS.PriceMapping;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Net.Http;

using global::ATAS.Indicators;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

[DisplayName("Futures Reference Price Axis / 期货现货映射轴")]
[Category(IndicatorCategories.Other)]
[Description("NQ/MNQ→QQQ and ES/MES→SPX time-aligned left reference price axis.")]
[HelpLink("https://docs.atas.net/en/")]
public sealed class FuturesReferencePriceAxisIndicator : Indicator
{
    private readonly ValueDataSeries _hiddenSeries = new("ReferencePriceAxisHidden")
    {
        VisualType = VisualMode.Hide,
        IsHidden = true,
        ScaleIt = false,
        ShowCurrentValue = false,
        ShowZeroValue = false
    };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<IReadOnlyList<MarketTradeSample>>>
        _historyRequests = new();
    private readonly object _priceSync = new();

    private CancellationTokenSource? _lifetimeCancellation;
    private CancellationTokenSource? _scheduleCancellation;
    private ITradesCache? _tradesCache;
    private MappingSnapshot? _successfulMapping;
    private UpdateAttemptSnapshot _latestAttempt =
        UpdateAttemptSnapshot.Waiting(DateTime.MinValue);
    private decimal _latestChartPrice;
    private long _generation;
    private bool _initialized;

    private PairMode _pairMode = Core.PairMode.Auto;
    private MappingMode _mappingMode = Core.MappingMode.Automatic;
    private decimal _manualRatio;
    private int _refreshIntervalMinutes = 30;
    private int _maxQuoteAgeMinutes = 20;
    private bool _showUpdateStatus = true;
    private int _axisWidth = 72;
    private DrawingColor _axisTextColor = DrawingColor.FromArgb(255, 226, 232, 240);
    private DrawingColor _axisBackgroundColor = DrawingColor.FromArgb(210, 22, 27, 34);
    private DrawingColor _axisBorderColor = DrawingColor.FromArgb(255, 92, 105, 121);
    private DrawingColor _statusBackgroundColor = DrawingColor.FromArgb(225, 18, 22, 28);

    public FuturesReferencePriceAxisIndicator()
        : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;
        DenyToChangePanel = true;
        DataSeries[0] = _hiddenSeries;
        DrawAbovePrice = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    [Display(
        Name = "Pair / 映射组合",
        GroupName = "Mapping / 映射",
        Order = 10)]
    public PairMode PairMode
    {
        get => _pairMode;
        set
        {
            if (_pairMode == value)
                return;

            _pairMode = value;
            Interlocked.Exchange(ref _successfulMapping, null);
            ConfigurationChanged(restartSchedule: false);
        }
    }

    [Display(
        Name = "Mode / 模式",
        GroupName = "Mapping / 映射",
        Order = 20)]
    public MappingMode MappingMode
    {
        get => _mappingMode;
        set
        {
            if (_mappingMode == value)
                return;

            _mappingMode = value;
            ConfigurationChanged(restartSchedule: true);
        }
    }

    [Display(
        Name = "Manual ratio / 手动比例",
        Description = "Futures price divided by QQQ/SPX price / 期货价除以参考价",
        GroupName = "Mapping / 映射",
        Order = 30)]
    [Range(typeof(decimal), "0", "1000000")]
    public decimal ManualRatio
    {
        get => _manualRatio;
        set
        {
            if (value < 0m || _manualRatio == value)
                return;

            _manualRatio = value;

            if (_mappingMode == Core.MappingMode.Manual)
            {
                SetAttempt(new UpdateAttemptSnapshot(
                    UpdateState.Manual,
                    CurrentMarketTime(),
                    "使用手动比例"));
            }

            RequestRedraw();
        }
    }

    [Display(
        Name = "Refresh interval (minutes) / 刷新分钟",
        GroupName = "Automatic / 自动",
        Order = 40)]
    [Range(1, 1440)]
    public int RefreshIntervalMinutes
    {
        get => _refreshIntervalMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 1440);

            if (_refreshIntervalMinutes == normalized)
                return;

            _refreshIntervalMinutes = normalized;
            ConfigurationChanged(restartSchedule: true);
        }
    }

    [Display(
        Name = "Max quote age (minutes) / 最大报价延迟",
        GroupName = "Automatic / 自动",
        Order = 50)]
    [Range(1, 240)]
    public int MaxQuoteAgeMinutes
    {
        get => _maxQuoteAgeMinutes;
        set
        {
            var normalized = Math.Clamp(value, 1, 240);

            if (_maxQuoteAgeMinutes == normalized)
                return;

            _maxQuoteAgeMinutes = normalized;
            ConfigurationChanged(restartSchedule: false);
        }
    }

    [Display(
        Name = "Show update status / 显示更新状态",
        GroupName = "Status / 状态",
        Order = 60)]
    public bool ShowUpdateStatus
    {
        get => _showUpdateStatus;
        set
        {
            _showUpdateStatus = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis width / 左轴宽度",
        GroupName = "Appearance / 外观",
        Order = 100)]
    [Range(45, 160)]
    public int AxisWidth
    {
        get => _axisWidth;
        set
        {
            _axisWidth = Math.Clamp(value, 45, 160);
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis text / 轴文字",
        GroupName = "Appearance / 外观",
        Order = 110)]
    public DrawingColor AxisTextColor
    {
        get => _axisTextColor;
        set
        {
            _axisTextColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis background / 轴背景",
        GroupName = "Appearance / 外观",
        Order = 120)]
    public DrawingColor AxisBackgroundColor
    {
        get => _axisBackgroundColor;
        set
        {
            _axisBackgroundColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Axis border / 轴边框",
        GroupName = "Appearance / 外观",
        Order = 130)]
    public DrawingColor AxisBorderColor
    {
        get => _axisBorderColor;
        set
        {
            _axisBorderColor = value;
            RequestRedraw();
        }
    }

    [Display(
        Name = "Status background / 状态背景",
        GroupName = "Appearance / 外观",
        Order = 140)]
    public DrawingColor StatusBackgroundColor
    {
        get => _statusBackgroundColor;
        set
        {
            _statusBackgroundColor = value;
            RequestRedraw();
        }
    }

    protected override void OnInitialize()
    {
        base.OnInitialize();

        _initialized = true;
        _lifetimeCancellation = new CancellationTokenSource();

        try
        {
            _tradesCache = GetTradesCache(TimeSpan.FromHours(2));
        }
        catch (Exception ex)
        {
            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Failed,
                CurrentMarketTime(),
                $"ATAS 成交缓存不可用: {ShortMessage(ex)}"));
        }

        RestartSchedule();
    }

    protected override void OnDispose()
    {
        _initialized = false;
        Interlocked.Increment(ref _generation);

        _scheduleCancellation?.Cancel();
        _scheduleCancellation?.Dispose();
        _scheduleCancellation = null;

        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;

        foreach (var request in _historyRequests.Values)
            request.TrySetCanceled();

        _historyRequests.Clear();
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

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || Container == null)
            return;

        var mapping = Volatile.Read(ref _successfulMapping);
        var attempt = Volatile.Read(ref _latestAttempt);
        var effectiveRatio = MappingMath.SelectEffectiveRatio(
            _mappingMode,
            _manualRatio,
            mapping);

        if (!InstrumentPairResolver.TryResolve(
                _pairMode,
                InstrumentInfo?.Instrument,
                out var pair))
        {
            if (_showUpdateStatus)
                DrawUnsupportedStatus(context, attempt);

            return;
        }

        if (effectiveRatio is > 0m)
            DrawReferenceAxis(context, pair, effectiveRatio.Value);

        if (_showUpdateStatus)
        {
            var statusMapping = _mappingMode == Core.MappingMode.Manual
                ? null
                : mapping;
            DrawStatusPanel(context, pair, attempt, statusMapping, effectiveRatio);
        }
    }

    private void RestartSchedule()
    {
        if (!_initialized || _lifetimeCancellation == null)
            return;

        _scheduleCancellation?.Cancel();
        _scheduleCancellation?.Dispose();
        _scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _ = RunRefreshLoopAsync(_scheduleCancellation.Token);
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(
                TimeSpan.FromMinutes(_refreshIntervalMinutes));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetFailure($"自动更新循环异常: {ShortMessage(ex)}");
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        if (_mappingMode != Core.MappingMode.Automatic)
        {
            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Manual,
                CurrentMarketTime(),
                "使用手动比例"));
            return;
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
                return;
            }

            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Fetching,
                CurrentMarketTime(),
                $"正在获取 {pair.ReferenceSymbol}"));

            var previous = Volatile.Read(ref _successfulMapping);
            var reference = await YahooChartClient.GetLatestCompletedAsync(
                    pair.YahooSymbol,
                    DateTime.UtcNow,
                    TimeSpan.FromMinutes(_maxQuoteAgeMinutes),
                    previous?.MinuteStartUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentGeneration(generation, cancellationToken))
                return;

            var marketMinute = ToMarketTime(reference.MinuteStartUtc);

            if (TryFindCachedClose(marketMinute, out var futuresClose))
            {
                ApplyMapping(generation, pair, reference, marketMinute, futuresClose);
                return;
            }

            var history = await RequestHistoricalTradesAsync(
                    marketMinute,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentGeneration(generation, cancellationToken))
                return;

            if (!MinuteCloseMatcher.TryFindClose(history, marketMinute, out futuresClose))
            {
                SetFailure($"ATAS 无 {marketMinute:yyyy-MM-dd HH:mm} 对应分钟成交");
                return;
            }

            ApplyMapping(generation, pair, reference, marketMinute, futuresClose);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (TimeoutException)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure("ATAS 历史成交请求超时");
        }
        catch (YahooDataException ex)
        {
            if (generation == Interlocked.Read(ref _generation))
            {
                var existing = Volatile.Read(ref _successfulMapping);

                if (ex.Code == "DuplicateQuote"
                    && string.Equals(
                        existing?.Pair.YahooSymbol,
                        "^GSPC",
                        StringComparison.OrdinalIgnoreCase))
                {
                    SetAttempt(new UpdateAttemptSnapshot(
                        UpdateState.Success,
                        CurrentMarketTime(),
                        "SPX 无新K，沿用最近收盘映射"));
                }
                else
                {
                    SetFailure(ex.Message);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure($"Yahoo 网络错误: {ShortMessage(ex)}");
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _generation))
                SetFailure($"自动更新失败: {ShortMessage(ex)}");
        }
        finally
        {
            if (enteredGate)
                _refreshGate.Release();
        }
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
                    pending.TrySetException(ex);
            }
        }

        try
        {
            if (DataProvider != null)
                DataProvider.DoActionInGuiThread(SubmitRequest);
            else
                SubmitRequest();

            return await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _historyRequests.TryRemove(request.RequestId, out _);
        }
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

        var applied = new MappingSnapshot(
            pair,
            reference.MinuteStartUtc,
            marketMinute,
            futuresClose,
            reference.Close,
            ratio,
            CurrentMarketTime());
        Volatile.Write(ref _successfulMapping, applied);
        var isLastSpxClose = string.Equals(
                                 pair.YahooSymbol,
                                 "^GSPC",
                                 StringComparison.OrdinalIgnoreCase)
                             && CurrentUtcTime() - reference.MinuteStartUtc.AddMinutes(1)
                             > TimeSpan.FromMinutes(_maxQuoteAgeMinutes);
        SetAttempt(new UpdateAttemptSnapshot(
            UpdateState.Success,
            applied.AppliedMarketTime,
            isLastSpxClose
                ? "SPX 最近收盘同分钟映射成功"
                : "同分钟映射更新成功"));
    }

    private void SetFailure(string message)
    {
        var state = Volatile.Read(ref _successfulMapping) == null
            ? UpdateState.Failed
            : UpdateState.Frozen;
        SetAttempt(new UpdateAttemptSnapshot(
            state,
            CurrentMarketTime(),
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

        if (!_initialized)
            return;

        if (_mappingMode == Core.MappingMode.Manual)
        {
            _scheduleCancellation?.Cancel();
            SetAttempt(new UpdateAttemptSnapshot(
                UpdateState.Manual,
                CurrentMarketTime(),
                "使用手动比例"));
        }
        else if (restartSchedule)
        {
            RestartSchedule();
        }
        else if (_scheduleCancellation != null)
        {
            _ = RefreshOnceAsync(_scheduleCancellation.Token);
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

    private DateTime CurrentUtcTime()
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

    private void RequestRedraw()
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

    private void DrawReferenceAxis(
        RenderContext context,
        InstrumentPair pair,
        decimal ratio)
    {
        var priceContainer = ChartInfo!.PriceChartContainer;
        var region = priceContainer.Region;

        if (region.Height <= 0 || region.Width <= 0 || ratio <= 0m)
            return;

        var width = Math.Min(_axisWidth, Math.Max(45, region.Width / 3));
        var axisRect = new Rectangle(region.X, region.Y, width, region.Height);
        context.FillRectangle(_axisBackgroundColor, axisRect);
        context.FillRectangle(
            _axisBorderColor,
            new Rectangle(axisRect.Right - 1, axisRect.Top, 1, axisRect.Height));

        var low = priceContainer.Low;
        var high = priceContainer.High;

        if (high <= low)
            return;

        var referenceLow = MappingMath.ToReferencePrice(low, ratio);
        var referenceHigh = MappingMath.ToReferencePrice(high, ratio);
        var step = TickGenerator.CalculateNiceStep(
            referenceLow,
            referenceHigh,
            region.Height);
        var font = ChartInfo.PriceAxisFont;
        var textFormat = new RenderStringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center
        };

        foreach (var tick in TickGenerator.EnumerateTicks(
                     referenceLow,
                     referenceHigh,
                     step))
        {
            var futuresPrice = MappingMath.ToFuturesPrice(tick, ratio);
            var y = ChartInfo.GetYByPrice(futuresPrice);

            if (y < axisRect.Top + 17 || y > axisRect.Bottom - 9)
                continue;

            context.FillRectangle(
                _axisBorderColor,
                new Rectangle(axisRect.Right - 7, y, 7, 1));

            var label = tick.ToString(
                $"F{pair.ReferenceDigits}",
                CultureInfo.InvariantCulture);
            var labelRect = new Rectangle(
                axisRect.Left + 2,
                y - 9,
                axisRect.Width - 10,
                18);
            context.DrawString(
                label,
                font,
                _axisTextColor,
                labelRect,
                textFormat);
        }

        context.FillRectangle(
            _axisBorderColor,
            new Rectangle(axisRect.Left, axisRect.Top, axisRect.Width, 17));
        context.DrawString(
            pair.ReferenceSymbol,
            font,
            DrawingColor.White,
            new Rectangle(axisRect.Left + 2, axisRect.Top, axisRect.Width - 4, 17),
            new RenderStringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            });

        decimal latestPrice;

        lock (_priceSync)
            latestPrice = _latestChartPrice;

        if (latestPrice <= 0m)
            return;

        var currentY = ChartInfo.GetYByPrice(latestPrice);

        if (currentY < axisRect.Top + 18 || currentY > axisRect.Bottom - 10)
            return;

        var currentReference = MappingMath.ToReferencePrice(latestPrice, ratio);
        var currentText = currentReference.ToString(
            $"F{pair.ReferenceDigits}",
            CultureInfo.InvariantCulture);
        var currentColor = DrawingColor.FromArgb(255, 47, 158, 101);
        var currentRect = new Rectangle(
            axisRect.Left,
            currentY - 10,
            axisRect.Width,
            20);
        context.FillRectangle(currentColor, currentRect);
        context.DrawString(
            currentText,
            font,
            DrawingColor.White,
            currentRect,
            new RenderStringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            });
    }

    private void DrawStatusPanel(
        RenderContext context,
        InstrumentPair pair,
        UpdateAttemptSnapshot attempt,
        MappingSnapshot? mapping,
        decimal? effectiveRatio)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var actualAxisWidth = Math.Min(_axisWidth, Math.Max(45, region.Width / 3));
        var availableWidth = region.Width - actualAxisWidth - 16;
        var panelWidth = Math.Min(330, availableWidth);

        if (panelWidth < 180 || region.Height < 130)
            return;

        var panelRect = new Rectangle(
            region.X + actualAxisWidth + 8,
            region.Y + 8,
            panelWidth,
            124);
        context.FillRectangle(_statusBackgroundColor, panelRect);
        DrawBorder(context, panelRect, _axisBorderColor);

        var isFrozen = attempt.State == UpdateState.Frozen;
        var stateText = StateText(attempt.State);
        var firstLine = $"最新更新: {stateText}";

        if (!string.IsNullOrWhiteSpace(attempt.Message))
            firstLine += $" — {attempt.Message}";

        var lines = new[]
        {
            firstLine,
            $"尝试时间: {FormatTime(attempt.AttemptMarketTime, includeSeconds: true)} [ATAS]",
            $"生效K时间: {FormatTime(mapping?.MinuteStartMarket, includeSeconds: false)} [ATAS]"
                + (isFrozen ? "（已冻结）" : string.Empty),
            $"{pair.FutureRoot}: {FormatPrice(mapping?.FuturesClose, 2)}",
            $"{pair.ReferenceSymbol}: {FormatPrice(mapping?.ReferenceClose, pair.ReferenceDigits)}",
            $"比例: {(effectiveRatio.HasValue ? effectiveRatio.Value.ToString("F8", CultureInfo.InvariantCulture) : "--")}"
        };

        var statusColor = StateColor(attempt.State);
        var font = ChartInfo.PriceAxisFont;
        var format = new RenderStringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };

        for (var i = 0; i < lines.Length; i++)
        {
            var lineRect = new Rectangle(
                panelRect.Left + 8,
                panelRect.Top + 5 + i * 19,
                panelRect.Width - 16,
                19);
            context.DrawString(
                lines[i],
                font,
                i == 0 ? statusColor : _axisTextColor,
                lineRect,
                format);
        }
    }

    private void DrawUnsupportedStatus(
        RenderContext context,
        UpdateAttemptSnapshot attempt)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var rect = new Rectangle(region.X + 8, region.Y + 8, 300, 48);
        context.FillRectangle(_statusBackgroundColor, rect);
        DrawBorder(context, rect, _axisBorderColor);
        context.DrawString(
            "无法识别期货标的，请选择 NQ/QQQ 或 ES/SPX",
            ChartInfo.PriceAxisFont,
            StateColor(UpdateState.Failed),
            new Rectangle(rect.Left + 8, rect.Top + 5, rect.Width - 16, 38),
            new RenderStringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center
            });
    }

    private static void DrawBorder(
        RenderContext context,
        Rectangle rect,
        DrawingColor color)
    {
        context.FillRectangle(color, new Rectangle(rect.Left, rect.Top, rect.Width, 1));
        context.FillRectangle(color, new Rectangle(rect.Left, rect.Bottom - 1, rect.Width, 1));
        context.FillRectangle(color, new Rectangle(rect.Left, rect.Top, 1, rect.Height));
        context.FillRectangle(color, new Rectangle(rect.Right - 1, rect.Top, 1, rect.Height));
    }

    private static DrawingColor StateColor(UpdateState state)
        => state switch
        {
            UpdateState.Success => DrawingColor.FromArgb(255, 72, 199, 142),
            UpdateState.Failed or UpdateState.Frozen =>
                DrawingColor.FromArgb(255, 243, 139, 75),
            UpdateState.Manual => DrawingColor.FromArgb(255, 91, 156, 246),
            _ => DrawingColor.FromArgb(255, 165, 174, 186)
        };

    private static string StateText(UpdateState state)
        => state switch
        {
            UpdateState.Waiting => "等待",
            UpdateState.Fetching => "获取中",
            UpdateState.Success => "成功",
            UpdateState.Failed => "失败",
            UpdateState.Frozen => "失败／已冻结",
            UpdateState.Manual => "手动模式",
            _ => state.ToString()
        };

    private static string FormatTime(DateTime? value, bool includeSeconds)
    {
        if (!value.HasValue || value.Value == DateTime.MinValue)
            return "--";

        return value.Value.ToString(
            includeSeconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture);
    }

    private static string FormatPrice(decimal? value, int digits)
    {
        if (!value.HasValue || value.Value <= 0m)
            return "--";

        return value.Value.ToString($"F{digits}", CultureInfo.InvariantCulture);
    }

    private static string ShortMessage(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 100 ? message : message[..100];
    }

    public override string ToString()
        => $"Reference Axis ({_mappingMode}, {_pairMode})";
}
