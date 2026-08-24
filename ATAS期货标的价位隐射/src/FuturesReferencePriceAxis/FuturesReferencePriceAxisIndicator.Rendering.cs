namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using System.Globalization;

using global::ATAS.Indicators;

using OFT.Rendering.Context;
using OFT.Rendering.Tools;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

public abstract partial class FuturesReferencePriceAxisIndicatorBase
{
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

        Rectangle? axisRect = null;

        if (effectiveRatio is > 0m)
            axisRect = DrawReferenceAxis(context, pair, effectiveRatio.Value);

        DrawEditionOverlay(context, pair, effectiveRatio, axisRect);

        if (_showUpdateStatus)
        {
            var statusMapping = _mappingMode == Core.MappingMode.Manual
                ? null
                : mapping;
            DrawStatusPanel(context, pair, attempt, statusMapping, effectiveRatio);
        }

        DrawEditionForeground(context, pair, effectiveRatio, axisRect);
    }

    protected virtual void DrawEditionOverlay(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
    }

    protected virtual void DrawEditionForeground(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
    }

    protected virtual int GetEditionReservedWidth(int actualAxisWidth)
        => 0;

    protected virtual int GetActualAxisWidth(int configuredAxisWidth, Rectangle region)
        => Math.Min(configuredAxisWidth, Math.Max(45, region.Width / 3));

    protected virtual IReadOnlyList<string> GetEditionStatusLines()
        => Array.Empty<string>();

    protected virtual int GetStatusPanelMaximumWidth()
        => 330;

    private Rectangle? DrawReferenceAxis(
        RenderContext context,
        InstrumentPair pair,
        decimal ratio)
    {
        var priceContainer = ChartInfo!.PriceChartContainer;
        var region = priceContainer.Region;

        if (region.Height <= 0 || region.Width <= 0 || ratio <= 0m)
            return null;

        var width = GetActualAxisWidth(_axisWidth, region);

        if (width <= 0)
            return null;

        var axisRect = new Rectangle(region.X, region.Y, width, region.Height);
        context.FillRectangle(_axisBackgroundColor, axisRect);
        context.FillRectangle(
            _axisBorderColor,
            new Rectangle(axisRect.Right - 1, axisRect.Top, 1, axisRect.Height));

        var low = priceContainer.Low;
        var high = priceContainer.High;

        if (high <= low)
            return axisRect;

        var font = ChartInfo.PriceAxisFont;
        var ticks = _axisLayoutCache.GetOrCreate(new AxisLayoutKey(
            low,
            high,
            ratio,
            width,
            region.Height,
            pair.ReferenceDigits));

        foreach (var tick in ticks)
        {
            var y = ChartInfo.GetYByPrice(tick.FuturesPrice);

            if (y < axisRect.Top + 17 || y > axisRect.Bottom - 9)
                continue;

            context.FillRectangle(
                _axisBorderColor,
                new Rectangle(axisRect.Right - 7, y, 7, 1));

            var labelRect = new Rectangle(
                axisRect.Left + 2,
                y - 9,
                axisRect.Width - 10,
                18);
            context.DrawString(
                tick.Text,
                font,
                _axisTextColor,
                labelRect,
                RightCenteredStringFormat);
        }

        context.FillRectangle(
            _axisBorderColor,
            new Rectangle(axisRect.Left, axisRect.Top, axisRect.Width, 17));
        context.DrawString(
            pair.ReferenceSymbol,
            font,
            DrawingColor.White,
            new Rectangle(axisRect.Left + 2, axisRect.Top, axisRect.Width - 4, 17),
            CenteredStringFormat);

        decimal latestPrice;

        lock (_priceSync)
            latestPrice = _latestChartPrice;

        if (latestPrice > 0m)
            DrawCurrentPriceLabel(context, pair, ratio, axisRect, font, latestPrice);

        if (_showCrosshairPriceLabel)
            DrawCrosshairPriceLabel(context, pair, ratio, axisRect, font);

        return axisRect;
    }

    private void DrawCurrentPriceLabel(
        RenderContext context,
        InstrumentPair pair,
        decimal ratio,
        Rectangle axisRect,
        RenderFont font,
        decimal latestPrice)
    {
        var currentY = ChartInfo!.GetYByPrice(latestPrice);

        if (currentY < axisRect.Top + 18 || currentY > axisRect.Bottom - 10)
            return;

        var labelKey = new CurrentLabelCacheKey(
            latestPrice,
            ratio,
            pair.ReferenceDigits);

        if (_currentLabelCacheKey != labelKey)
        {
            var currentReference = MappingMath.ToReferencePrice(latestPrice, ratio);
            _currentLabelCache = currentReference.ToString(
                $"F{pair.ReferenceDigits}",
                CultureInfo.InvariantCulture);
            _currentLabelCacheKey = labelKey;
        }

        var currentRect = new Rectangle(
            axisRect.Left,
            currentY - 10,
            axisRect.Width,
            20);
        context.FillRectangle(CurrentPriceLabelColor, currentRect);
        context.DrawString(
            _currentLabelCache,
            font,
            DrawingColor.White,
            currentRect,
            CenteredStringFormat);
    }

    private void DrawCrosshairPriceLabel(
        RenderContext context,
        InstrumentPair pair,
        decimal ratio,
        Rectangle axisRect,
        RenderFont font)
    {
        var mouse = MouseLocationInfo;

        if (mouse == null || mouse.IsMouseLeave || mouse.IsMovingChartUsingMouse)
            return;

        var position = mouse.LastPosition;

        if (position.X < ChartInfo!.PriceChartContainer.Region.Left
            || position.X >= ChartInfo.PriceChartContainer.Region.Right
            || position.Y < axisRect.Top + 18
            || position.Y > axisRect.Bottom - 10)
        {
            return;
        }

        var futuresPrice = mouse.PriceBelowMouse;

        if (futuresPrice <= 0m)
            return;

        var referencePrice = MappingMath.ToReferencePrice(futuresPrice, ratio);
        var text = referencePrice.ToString(
            $"F{pair.ReferenceDigits}",
            CultureInfo.InvariantCulture);
        var rect = new Rectangle(
            axisRect.Left,
            position.Y - 10,
            axisRect.Width,
            20);
        var colors = ChartInfo.ColorsStore;
        context.FillRectangle(colors.MouseBackground, rect);
        DrawBorder(context, rect, colors.MouseTextColor);
        context.DrawString(
            text,
            font,
            colors.MouseTextColor,
            rect,
            CenteredStringFormat);
    }

    private void DrawStatusPanel(
        RenderContext context,
        InstrumentPair pair,
        UpdateAttemptSnapshot attempt,
        MappingSnapshot? mapping,
        decimal? effectiveRatio)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var actualAxisWidth = GetActualAxisWidth(_axisWidth, region);
        var editionWidth = GetEditionReservedWidth(actualAxisWidth);
        var reservedWidth = actualAxisWidth + editionWidth;
        var availableWidth = region.Width - reservedWidth - 16;
        var panelWidth = Math.Min(GetStatusPanelMaximumWidth(), availableWidth);
        var baseLines = GetStatusLines(pair, attempt, mapping, effectiveRatio);
        var editionLines = GetEditionStatusLines();
        var lines = editionLines.Count == 0
            ? baseLines
            : baseLines.Concat(editionLines).ToArray();
        var panelHeight = 10 + lines.Length * 19;

        if (panelWidth < 180 || region.Height < panelHeight + 6)
            return;

        var panelRect = OffsetAndClampStatusRect(
            region,
            region.X + reservedWidth + 8,
            region.Y + 8,
            panelWidth,
            panelHeight);
        context.FillRectangle(_statusBackgroundColor, panelRect);
        DrawBorder(context, panelRect, _axisBorderColor);

        var statusColor = StateColor(attempt.State);
        var font = ChartInfo.PriceAxisFont;

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
                LeftCenteredStringFormat);
        }
    }

    private void DrawUnsupportedStatus(
        RenderContext context,
        UpdateAttemptSnapshot attempt)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var width = Math.Min(300, Math.Max(1, region.Width - 8));
        var rect = OffsetAndClampStatusRect(
            region,
            region.X + 8,
            region.Y + 8,
            width,
            48);
        context.FillRectangle(_statusBackgroundColor, rect);
        DrawBorder(context, rect, _axisBorderColor);
        context.DrawString(
            "无法识别期货标的，请选择 NQ/QQQ 或 ES/SPX",
            ChartInfo.PriceAxisFont,
            StateColor(UpdateState.Failed),
            new Rectangle(rect.Left + 8, rect.Top + 5, rect.Width - 16, 38),
            LeftCenteredStringFormat);
    }

    private string[] GetStatusLines(
        InstrumentPair pair,
        UpdateAttemptSnapshot attempt,
        MappingSnapshot? mapping,
        decimal? effectiveRatio)
    {
        var key = new StatusTextCacheKey(
            pair,
            attempt,
            mapping,
            effectiveRatio,
            _uiUtcOffsetHours);

        if (_statusTextCacheKey == key)
            return _statusTextCache;

        var isFrozen = attempt.State == UpdateState.Frozen;
        var firstLine = $"最新更新: {StateText(attempt.State)}";
        var utcLabel = FormatUtcOffsetLabel();

        if (!string.IsNullOrWhiteSpace(attempt.Message))
            firstLine += $" — {attempt.Message}";

        _statusTextCache =
        [
            firstLine,
            $"尝试时间: {FormatUiTime(attempt.AttemptUtcTime, includeSeconds: true)} [{utcLabel}]",
            $"生效K时间: {FormatUiTime(mapping?.MinuteStartUtc, includeSeconds: false)} [{utcLabel}]"
                + (isFrozen ? "（已冻结）" : string.Empty),
            $"{pair.FutureRoot}: {FormatPrice(mapping?.FuturesClose, 2)}",
            $"{pair.ReferenceSymbol}: {FormatPrice(mapping?.ReferenceClose, pair.ReferenceDigits)}",
            $"比例: {(effectiveRatio.HasValue ? effectiveRatio.Value.ToString("F8", CultureInfo.InvariantCulture) : "--")}"
        ];
        _statusTextCacheKey = key;
        return _statusTextCache;
    }

    private Rectangle OffsetAndClampStatusRect(
        Rectangle region,
        int anchorX,
        int anchorY,
        int width,
        int height)
    {
        const int margin = 4;
        var minLeft = region.Left + margin;
        var minTop = region.Top + margin;
        var maxLeft = Math.Max(minLeft, region.Right - width - margin);
        var maxTop = Math.Max(minTop, region.Bottom - height - margin);
        var left = Math.Clamp(anchorX + _statusPanelOffsetX, minLeft, maxLeft);
        var top = Math.Clamp(anchorY + _statusPanelOffsetY, minTop, maxTop);
        return new Rectangle(left, top, width, height);
    }

    protected static void DrawBorder(
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

    protected string FormatUiTime(DateTime? utcValue, bool includeSeconds)
    {
        if (!utcValue.HasValue || utcValue.Value == DateTime.MinValue)
            return "--";

        var displayTime = UiTimeZoneFormatter.ConvertFromUtc(
            utcValue.Value,
            _uiUtcOffsetHours);
        return displayTime.ToString(
            includeSeconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture);
    }

    protected string FormatUtcOffsetLabel()
        => UiTimeZoneFormatter.FormatOffsetLabel(_uiUtcOffsetHours);

    private static string FormatPrice(decimal? value, int digits)
    {
        if (!value.HasValue || value.Value <= 0m)
            return "--";

        return value.Value.ToString($"F{digits}", CultureInfo.InvariantCulture);
    }

}
