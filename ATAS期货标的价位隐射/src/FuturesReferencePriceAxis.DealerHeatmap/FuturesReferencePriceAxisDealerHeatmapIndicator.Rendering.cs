namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using System.Globalization;

using global::ATAS.Indicators;

using OFT.Rendering.Context;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private const int HeatmapHeaderHeight = 18;
    private static readonly DrawingColor HeatmapBackground =
        DrawingColor.FromArgb(220, 18, 22, 28);
    private static readonly DrawingColor HeatmapHeaderBackground =
        DrawingColor.FromArgb(255, 35, 41, 50);

    protected override int GetEditionReservedWidth(int actualAxisWidth)
        => _showDealerHeatmap ? actualAxisWidth : 0;

    protected override int GetStatusPanelMaximumWidth()
        => 480;

    protected override IReadOnlyList<string> GetEditionStatusLines()
    {
        if (!_showDealerHeatmap)
            return Array.Empty<string>();

        var snapshot = Volatile.Read(ref _dealerHeatmapSnapshot);
        var target = snapshot.Target is { } value
            ? $"{value.Ticker} {value.TargetExpiration:yyyy-MM-dd}"
            : "--";
        var asOf = snapshot.Frame == null
            ? "--"
            : FormatUiTime(snapshot.Frame.MinuteAtUtc, includeSeconds: false);
        var next = snapshot.NextAttemptUtc.HasValue
            ? FormatUiTime(snapshot.NextAttemptUtc, includeSeconds: true)
            : "--";
        var cellCount = snapshot.Frame?.Cells.Count ?? 0;
        return new[]
        {
            $"Heatmap: {target}",
            $"Heatmap 状态: {StateLabel(snapshot.State)}；节点: {cellCount}",
            $"数据时间: {asOf} [{FormatUtcOffsetLabel()}]",
            $"下次更新: {next}",
            $"Heatmap 信息: {snapshot.Message}"
        };
    }

    protected override void DrawEditionOverlay(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
        if (!_showDealerHeatmap || effectiveRatio is not > 0m || axisRect == null)
            return;

        var heatmapRect = GetHeatmapRectangle(axisRect.Value);

        if (heatmapRect.Width < 3 || heatmapRect.Height <= HeatmapHeaderHeight)
            return;

        context.FillRectangle(HeatmapBackground, heatmapRect);
        DrawBorder(context, heatmapRect, ConfiguredAxisBorderColor);
        context.FillRectangle(
            HeatmapHeaderBackground,
            new Rectangle(
                heatmapRect.Left + 1,
                heatmapRect.Top + 1,
                heatmapRect.Width - 2,
                HeatmapHeaderHeight - 1));

        var snapshot = Volatile.Read(ref _dealerHeatmapSnapshot);
        DrawHeatmapHeader(context, heatmapRect, snapshot);

        if (snapshot.Target is not { } target
            || !IsFrameForTarget(snapshot.Frame, target)
            || snapshot.Frame is not { } frame)
        {
            DrawHeatmapMessage(context, heatmapRect, snapshot.Message);
            return;
        }

        var minimum = Math.Min(0m, frame.Cells.Min(static cell => cell.NetDealerGexUsd));
        var maximum = Math.Max(0m, frame.Cells.Max(static cell => cell.NetDealerGexUsd));

        foreach (var cell in frame.Cells)
        {
            var cellRect = GetCellRectangle(
                heatmapRect,
                frame.Ticker,
                cell.StrikeUsd,
                effectiveRatio.Value);

            if (cellRect == null)
                continue;

            var rgb = DealerHeatmapPresentation.GetColor(
                cell.NetDealerGexUsd,
                minimum,
                maximum);
            var background = DrawingColor.FromArgb(248, rgb.Red, rgb.Green, rgb.Blue);
            context.FillRectangle(background, cellRect.Value);

            if (cellRect.Value.Height < 14)
                continue;

            var textColor = DealerHeatmapPresentation.UseDarkText(rgb)
                ? DrawingColor.Black
                : DrawingColor.White;
            context.DrawString(
                DealerHeatmapPresentation.FormatGex(cell.NetDealerGexUsd),
                ChartInfo!.PriceAxisFont,
                textColor,
                cellRect.Value,
                CenteredStringFormat);
        }
    }

    protected override void DrawEditionForeground(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
        if (!_showDealerHeatmap || effectiveRatio is not > 0m || axisRect == null)
            return;

        var heatmapRect = GetHeatmapRectangle(axisRect.Value);

        if (heatmapRect.Width < 3 || heatmapRect.Height <= HeatmapHeaderHeight)
            return;

        DrawMappedPriceLines(context, heatmapRect);
        DrawHeatmapTooltip(context, heatmapRect, effectiveRatio.Value);
    }

    private Rectangle GetHeatmapRectangle(Rectangle axisRect)
        => new(axisRect.Right, axisRect.Top, axisRect.Width, axisRect.Height);

    private void DrawHeatmapHeader(
        RenderContext context,
        Rectangle heatmapRect,
        DealerHeatmapSnapshot snapshot)
    {
        var dataTime = snapshot.Frame == null
            ? "--"
            : FormatHeatmapTime(snapshot.Frame.MinuteAtUtc);
        context.DrawString(
            dataTime,
            ChartInfo!.PriceAxisFont,
            ConfiguredAxisTextColor,
            new Rectangle(
                heatmapRect.Left + 3,
                heatmapRect.Top + 1,
                heatmapRect.Width - 6,
                16),
            CenteredStringFormat);
    }

    private void DrawHeatmapMessage(
        RenderContext context,
        Rectangle heatmapRect,
        string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "等待数据" : message;
        context.DrawString(
            text,
            ChartInfo!.PriceAxisFont,
            ConfiguredAxisTextColor,
            new Rectangle(
                heatmapRect.Left + 4,
                heatmapRect.Top + HeatmapHeaderHeight + 6,
                heatmapRect.Width - 8,
                42),
            CenteredStringFormat);
    }

    private Rectangle? GetCellRectangle(
        Rectangle heatmapRect,
        string ticker,
        decimal strike,
        decimal ratio)
    {
        var bounds = DealerHeatmapPresentation.GetReferenceBounds(ticker, strike);
        var chart = ChartInfo!;
        var firstY = chart.GetYByPrice(bounds.Upper * ratio);
        var secondY = chart.GetYByPrice(bounds.Lower * ratio);
        var rawTop = Math.Min(firstY, secondY);
        var rawBottom = Math.Max(firstY, secondY);
        var top = Math.Max(heatmapRect.Top + HeatmapHeaderHeight, rawTop);
        var bottom = Math.Min(heatmapRect.Bottom - 1, rawBottom);

        if (bottom <= top)
            return null;

        return new Rectangle(
            heatmapRect.Left + 1,
            top,
            Math.Max(1, heatmapRect.Width - 2),
            Math.Max(1, bottom - top));
    }

    private void DrawMappedPriceLines(RenderContext context, Rectangle heatmapRect)
    {
        var latestPrice = LatestChartPrice;

        if (latestPrice > 0m)
        {
            var y = ChartInfo!.GetYByPrice(latestPrice);

            if (y >= heatmapRect.Top + HeatmapHeaderHeight && y < heatmapRect.Bottom)
            {
                DrawDashedHorizontalLine(
                    context,
                    heatmapRect.Left,
                    heatmapRect.Right,
                    y,
                    DrawingColor.FromArgb(255, 47, 158, 101));
            }
        }

        var mouse = MouseLocationInfo;

        if (!ShowCrosshairPriceLabel
            || mouse == null
            || mouse.IsMouseLeave
            || mouse.IsMovingChartUsingMouse)
        {
            return;
        }

        var position = mouse.LastPosition;

        if (position.Y >= heatmapRect.Top + HeatmapHeaderHeight
            && position.Y < heatmapRect.Bottom)
        {
            DrawDashedHorizontalLine(
                context,
                heatmapRect.Left,
                heatmapRect.Right,
                position.Y,
                ChartInfo!.ColorsStore.MouseTextColor);
        }
    }

    private void DrawHeatmapTooltip(
        RenderContext context,
        Rectangle heatmapRect,
        decimal ratio)
    {
        var mouse = MouseLocationInfo;

        if (mouse == null || mouse.IsMouseLeave || mouse.IsMovingChartUsingMouse)
            return;

        var position = mouse.LastPosition;

        if (!heatmapRect.Contains(position))
            return;

        var snapshot = Volatile.Read(ref _dealerHeatmapSnapshot);

        if (snapshot.Target is not { } target
            || !IsFrameForTarget(snapshot.Frame, target)
            || snapshot.Frame is not { } frame)
        {
            return;
        }

        DealerHeatmapCell? hovered = null;

        foreach (var cell in frame.Cells)
        {
            var rect = GetCellRectangle(
                heatmapRect,
                frame.Ticker,
                cell.StrikeUsd,
                ratio);

            if (rect?.Contains(position) == true)
            {
                hovered = cell;
                break;
            }
        }

        if (!hovered.HasValue)
            return;

        var region = ChartInfo!.PriceChartContainer.Region;
        var width = Math.Max(1, Math.Min(270, region.Width - 8));
        var height = Math.Max(1, Math.Min(92, region.Height - 8));
        var left = Math.Clamp(
            position.X + 12,
            region.Left + 4,
            Math.Max(region.Left + 4, region.Right - width - 4));
        var top = Math.Clamp(
            position.Y + 12,
            region.Top + 4,
            Math.Max(region.Top + 4, region.Bottom - height - 4));
        var tooltip = new Rectangle(left, top, width, height);
        context.FillRectangle(
            DrawingColor.FromArgb(245, 18, 22, 28),
            tooltip);
        DrawBorder(context, tooltip, ConfiguredAxisBorderColor);
        var cellValue = hovered.Value;
        var lines = new[]
        {
            $"{frame.Ticker}  Strike {cellValue.StrikeUsd.ToString("0.##", CultureInfo.InvariantCulture)}",
            $"GEX {FormatFullGex(cellValue.NetDealerGexUsd)}",
            $"Expiration {frame.Expiration:yyyy-MM-dd}",
            $"As-of {FormatHeatmapTime(frame.MinuteAtUtc)}"
        };

        for (var index = 0; index < lines.Length; index++)
        {
            context.DrawString(
                lines[index],
                ChartInfo.PriceAxisFont,
                index == 1 ? DrawingColor.White : ConfiguredAxisTextColor,
                new Rectangle(
                    tooltip.Left + 8,
                    tooltip.Top + 5 + index * 20,
                    tooltip.Width - 16,
                    20));
        }
    }

    private static void DrawDashedHorizontalLine(
        RenderContext context,
        int left,
        int right,
        int y,
        DrawingColor color)
    {
        for (var x = left; x < right; x += 6)
        {
            context.FillRectangle(
                color,
                new Rectangle(x, y, Math.Min(3, right - x), 1));
        }
    }

    private string FormatHeatmapTime(DateTime utcTime)
    {
        var display = UiTimeZoneFormatter.ConvertFromUtc(
            utcTime,
            ConfiguredUiUtcOffsetHours);
        return display.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatFullGex(decimal value)
    {
        var prefix = value < 0m ? "-" : string.Empty;
        return prefix + Math.Abs(value).ToString("#,0.##", CultureInfo.InvariantCulture);
    }

    private static string StateLabel(DealerHeatmapState state)
        => state switch
        {
            DealerHeatmapState.Live => "LIVE",
            DealerHeatmapState.NextSession => "NEXT SESSION",
            DealerHeatmapState.Frozen => "FROZEN",
            DealerHeatmapState.AuthenticationFailed => "AUTH FAILED",
            DealerHeatmapState.RateLimited => "RATE LIMITED",
            DealerHeatmapState.Fetching => "FETCHING",
            DealerHeatmapState.MissingApiKey => "NO KEY",
            DealerHeatmapState.Disabled => "OFF",
            _ => "WAITING"
        };
}
