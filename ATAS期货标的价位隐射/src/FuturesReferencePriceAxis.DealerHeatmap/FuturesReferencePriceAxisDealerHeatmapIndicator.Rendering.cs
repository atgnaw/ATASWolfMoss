namespace WolfMoss.ATAS.PriceMapping;

using System.Drawing;
using System.Globalization;

using global::ATAS.Indicators;

using OFT.Rendering.Context;

using WolfMoss.ATAS.PriceMapping.Core;

using DrawingColor = System.Drawing.Color;

public sealed partial class FuturesReferencePriceAxisDealerHeatmapIndicator
{
    private const int DealerHeaderHeight = 18;
    private const int StructureLabelHeight = 14;
    private static readonly DrawingColor DealerColumnBackground =
        DrawingColor.FromArgb(220, 18, 22, 28);
    private static readonly DrawingColor DealerHeaderBackground =
        DrawingColor.FromArgb(255, 35, 41, 50);
    private static readonly DrawingColor DealerGexTrackBackground =
        DrawingColor.FromArgb(190, 43, 49, 59);
    private static readonly DrawingColor GammaFlipColor =
        DrawingColor.FromArgb(255, 250, 204, 21);
    private static readonly DrawingColor CallWallColor =
        DrawingColor.FromArgb(255, 52, 211, 153);
    private static readonly DrawingColor PutWallColor =
        DrawingColor.FromArgb(255, 248, 81, 73);

    protected override int GetActualAxisWidth(int configuredAxisWidth, Rectangle region)
        => DealerColumnPlanner.CalculateColumnWidth(
            configuredAxisWidth,
            region.Width,
            VisibleDealerColumns, RegisteredColumnCatalog);

    protected override int GetEditionReservedWidth(int actualAxisWidth)
        => actualAxisWidth * RegisteredColumnCatalog.Count(VisibleDealerColumns);

    protected override int GetStatusPanelMaximumWidth()
        => 480;

    protected override IReadOnlyList<string> GetEditionStatusLines()
    {
        var performanceLines = Volatile.Read(ref _performanceLines);
        var visibility = VisibleDealerColumns;
        _statusSnapshots ??= new object?[RegisteredColumns.Length];
        for (var i = 0; i < RegisteredColumns.Length; i++)
            _statusSnapshots[i] = (visibility & RegisteredColumns[i].Visibility) == 0 ? null : RegisteredColumns[i].Snapshot(this);
        var height = ChartInfo?.PriceChartContainer.Region.Height ?? int.MaxValue / 2;
        var activeLines = _showOptionPremiumFlow ? GetOptionActiveLineCount() : 0;
        if (TryGetCachedEditionStatusLines(visibility, _statusSnapshots, performanceLines, height, activeLines, out var cachedLines))
            return cachedLines;
        var lines = new List<string>(16);
        for (var i = 0; i < RegisteredColumns.Length; i++)
            if (_statusSnapshots[i] is { } snapshot) RegisteredColumns[i].Status(this, lines, snapshot, activeLines);
        var diagnosticRoom = Math.Max(0, (height - 20) / 19 - 6 - lines.Count);
        if (diagnosticRoom >= performanceLines.Length) lines.AddRange(performanceLines);
        else if (diagnosticRoom > 0)
        {
            lines.AddRange(performanceLines.Take(diagnosticRoom - 1));
            lines.Add("PERF: 增高图表查看完整摘要；Record 可保存文件");
        }
        return CacheEditionStatusLines(visibility, _statusSnapshots, height, activeLines, lines, performanceLines);
    }

    protected override void DrawEditionOverlay(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
        if (effectiveRatio is not > 0m || axisRect == null)
            return;

        var columns = DealerColumnPlanner.Create(
            axisRect.Value.Right,
            axisRect.Value.Width,
            VisibleDealerColumns, RegisteredColumnCatalog);

        foreach (var column in RegisteredColumns)
            if (columns.TryGetLeft(column.Kind, out var left))
                column.Draw(this, context, GetColumnRectangle(axisRect.Value, left), effectiveRatio.Value);
    }

    protected override void DrawEditionForeground(
        RenderContext context,
        InstrumentPair pair,
        decimal? effectiveRatio,
        Rectangle? axisRect)
    {
        if (effectiveRatio is not > 0m || axisRect == null)
            return;

        var columns = DealerColumnPlanner.Create(
            axisRect.Value.Right,
            axisRect.Value.Width,
            VisibleDealerColumns, RegisteredColumnCatalog);

        if (columns.ColumnCount == 0)
            return;

        var dataRect = new Rectangle(
            axisRect.Value.Right,
            axisRect.Value.Top,
            axisRect.Value.Width * columns.ColumnCount,
            axisRect.Value.Height);
        DrawMappedPriceLines(context, dataRect);

        foreach (var column in RegisteredColumns)
            if (columns.TryGetLeft(column.Kind, out var left))
                column.Tooltip(this, context, GetColumnRectangle(axisRect.Value, left), effectiveRatio.Value);
    }

    private static Rectangle GetColumnRectangle(Rectangle axisRect, int left)
        => new(left, axisRect.Top, axisRect.Width, axisRect.Height);

    private void DrawHeatmapColumn(
        RenderContext context,
        Rectangle heatmapRect,
        decimal effectiveRatio)
    {
        if (!CanDrawColumn(heatmapRect))
            return;

        DrawColumnBackground(context, heatmapRect);
        var snapshot = Volatile.Read(ref _dealerHeatmapSnapshot);
        DrawColumnHeader(context, heatmapRect, snapshot.Frame?.MinuteAtUtc);

        if (snapshot.Target is not { } target
            || !IsFrameForTarget(snapshot.Frame, target)
            || snapshot.Frame is not { } frame)
        {
            DrawColumnMessage(context, heatmapRect, snapshot.Message);
            return;
        }

        var valueRange = _dealerRenderMetricsCache.GetHeatmapRange(frame);

        for (var cellIndex = 0; cellIndex < frame.Cells.Count; cellIndex++)
        {
            var cell = frame.Cells[cellIndex];
            var cellRect = GetHeatmapCellRectangle(
                heatmapRect,
                frame,
                cellIndex,
                effectiveRatio);

            if (cellRect == null)
                continue;

            var rgb = DealerHeatmapPresentation.GetColor(
                cell.NetDealerGexUsd,
                valueRange.Minimum,
                valueRange.Maximum);
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

    private void DrawDealerGexColumn(
        RenderContext context,
        Rectangle dealerGexRect,
        decimal effectiveRatio)
    {
        if (!CanDrawColumn(dealerGexRect))
            return;

        DrawColumnBackground(context, dealerGexRect);
        var snapshot = Volatile.Read(ref _dealerGexSnapshot);
        DrawColumnHeader(context, dealerGexRect, snapshot.Frame?.SnapshotAtUtc);

        if (snapshot.Ticker == null
            || !IsDealerGexFrameForTicker(snapshot.Frame, snapshot.Ticker)
            || snapshot.Frame is not { } frame)
        {
            DrawColumnMessage(context, dealerGexRect, snapshot.Message);
            return;
        }

        var maximumAbsolute = _dealerRenderMetricsCache.GetDealerGexMaximumAbsolute(frame);

        foreach (var node in frame.Nodes)
        {
            var rowRect = GetStrikeRectangle(
                dealerGexRect,
                frame.Ticker,
                node.StrikeUsd,
                effectiveRatio);

            if (rowRect == null)
                continue;

            context.FillRectangle(DealerGexTrackBackground, rowRect.Value);
            var fillRatio = DealerGexPresentation.GetFillRatio(
                node.NetGexUsd,
                maximumAbsolute);
            var fillWidth = (int)Math.Round(rowRect.Value.Width * fillRatio);

            if (fillWidth <= 0)
                continue;

            var rgb = DealerGexPresentation.GetNodeColor(
                node.NetGexUsd,
                node.NodeType);
            var fillRect = new Rectangle(
                rowRect.Value.Left,
                rowRect.Value.Top,
                Math.Min(rowRect.Value.Width, fillWidth),
                rowRect.Value.Height);
            context.FillRectangle(
                DrawingColor.FromArgb(245, rgb.Red, rgb.Green, rgb.Blue),
                fillRect);

            if (fillRect.Height < 14 || fillRect.Width < 32)
                continue;

            context.DrawString(
                DealerGexPresentation.FormatStrike(node.StrikeUsd),
                ChartInfo!.PriceAxisFont,
                DealerGexPresentation.UseDarkText(node.NodeType)
                    ? DrawingColor.Black
                    : DrawingColor.White,
                fillRect,
                CenteredStringFormat);
        }

        DrawDealerGexStructureLines(
            context,
            dealerGexRect,
            effectiveRatio,
            frame.Summary);
    }

    private bool CanDrawColumn(Rectangle rect)
        => rect.Width >= 3 && rect.Height > DealerHeaderHeight;

    private void DrawColumnBackground(RenderContext context, Rectangle rect)
    {
        context.FillRectangle(DealerColumnBackground, rect);
        DrawBorder(context, rect, ConfiguredAxisBorderColor);
        context.FillRectangle(
            DealerHeaderBackground,
            new Rectangle(
                rect.Left + 1,
                rect.Top + 1,
                rect.Width - 2,
                DealerHeaderHeight - 1));
    }

    private void DrawColumnHeader(
        RenderContext context,
        Rectangle rect,
        DateTime? dataTimeUtc)
    {
        var text = dataTimeUtc.HasValue
            ? FormatDealerTime(dataTimeUtc.Value)
            : "--";
        context.DrawString(
            text,
            ChartInfo!.PriceAxisFont,
            ConfiguredAxisTextColor,
            new Rectangle(rect.Left + 3, rect.Top + 1, rect.Width - 6, 16),
            CenteredStringFormat);
    }

    private void DrawColumnMessage(
        RenderContext context,
        Rectangle rect,
        string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "等待数据" : message;
        context.DrawString(
            text,
            ChartInfo!.PriceAxisFont,
            ConfiguredAxisTextColor,
            new Rectangle(
                rect.Left + 4,
                rect.Top + DealerHeaderHeight + 6,
                rect.Width - 8,
                42),
            CenteredStringFormat);
    }

    private Rectangle? GetStrikeRectangle(
        Rectangle columnRect,
        string ticker,
        decimal strike,
        decimal ratio)
    {
        var bounds = DealerHeatmapPresentation.GetReferenceBounds(ticker, strike);
        return GetStrikeRectangle(columnRect, bounds, ratio);
    }

    private Rectangle? GetHeatmapCellRectangle(
        Rectangle columnRect,
        DealerHeatmapFrame frame,
        int cellIndex,
        decimal ratio)
    {
        var cell = frame.Cells[cellIndex];
        var lowerNeighbor = cellIndex > 0
            ? frame.Cells[cellIndex - 1].StrikeUsd
            : (decimal?)null;
        var upperNeighbor = cellIndex + 1 < frame.Cells.Count
            ? frame.Cells[cellIndex + 1].StrikeUsd
            : (decimal?)null;
        var bounds = DealerHeatmapPresentation.GetAdaptiveReferenceBounds(
            frame.Ticker,
            cell.StrikeUsd,
            lowerNeighbor,
            upperNeighbor);

        return GetStrikeRectangle(columnRect, bounds, ratio);
    }

    private Rectangle? GetStrikeRectangle(
        Rectangle columnRect,
        (decimal Lower, decimal Upper) bounds,
        decimal ratio)
    {
        var chart = ChartInfo!;
        var firstY = chart.GetYByPrice(bounds.Upper * ratio);
        var secondY = chart.GetYByPrice(bounds.Lower * ratio);
        var rawTop = Math.Min(firstY, secondY);
        var rawBottom = Math.Max(firstY, secondY);
        var top = Math.Max(columnRect.Top + DealerHeaderHeight, rawTop);
        var bottom = Math.Min(columnRect.Bottom - 1, rawBottom);

        if (bottom <= top)
            return null;

        return new Rectangle(
            columnRect.Left + 1,
            top,
            Math.Max(1, columnRect.Width - 2),
            Math.Max(1, bottom - top));
    }

    private void DrawDealerGexStructureLines(
        RenderContext context,
        Rectangle rect,
        decimal ratio,
        DealerGexSummary summary)
    {
        var definitions = new[]
        {
            new StructureLine("GF", summary.GammaFlipUsd, GammaFlipColor, true),
            new StructureLine("CW", summary.CallWallStrikeUsd, CallWallColor, false),
            new StructureLine("PW", summary.PutWallStrikeUsd, PutWallColor, false)
        };
        var visible = definitions
            .Where(static line => line.ReferencePrice is > 0m)
            .Select(line => line with
            {
                Y = ChartInfo!.GetYByPrice(line.ReferencePrice!.Value * ratio)
            })
            .Where(line => line.Y >= rect.Top + DealerHeaderHeight
                           && line.Y < rect.Bottom)
            .ToArray();

        if (visible.Length == 0)
            return;

        var labelTops = DealerGexPresentation.ResolveLabelTops(
            visible.Select(static line => line.Y).ToArray(),
            rect.Top + DealerHeaderHeight,
            rect.Bottom,
            StructureLabelHeight,
            1);

        for (var index = 0; index < visible.Length; index++)
        {
            var line = visible[index];

            if (line.Dashed)
            {
                DrawDashedHorizontalLine(
                    context,
                    rect.Left + 1,
                    rect.Right - 1,
                    line.Y,
                    line.Color,
                    2);
            }
            else
            {
                context.FillRectangle(
                    line.Color,
                    new Rectangle(rect.Left + 1, line.Y, rect.Width - 2, 2));
            }

            var labelRect = new Rectangle(
                rect.Left + 3,
                labelTops[index],
                Math.Min(26, Math.Max(1, rect.Width - 6)),
                StructureLabelHeight);
            context.FillRectangle(DrawingColor.FromArgb(240, 18, 22, 28), labelRect);
            context.DrawString(
                line.Label,
                ChartInfo!.PriceAxisFont,
                line.Color,
                labelRect,
                CenteredStringFormat);
        }
    }

    private void DrawMappedPriceLines(RenderContext context, Rectangle dataRect)
    {
        var latestPrice = LatestChartPrice;

        if (latestPrice > 0m)
        {
            var y = ChartInfo!.GetYByPrice(latestPrice);

            if (y >= dataRect.Top + DealerHeaderHeight && y < dataRect.Bottom)
            {
                DrawDashedHorizontalLine(
                    context,
                    dataRect.Left,
                    dataRect.Right,
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

        if (position.Y >= dataRect.Top + DealerHeaderHeight
            && position.Y < dataRect.Bottom)
        {
            DrawDashedHorizontalLine(
                context,
                dataRect.Left,
                dataRect.Right,
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

        for (var cellIndex = 0; cellIndex < frame.Cells.Count; cellIndex++)
        {
            var cell = frame.Cells[cellIndex];
            var rect = GetHeatmapCellRectangle(heatmapRect, frame, cellIndex, ratio);

            if (rect?.Contains(position) == true)
            {
                hovered = cell;
                break;
            }
        }

        if (!hovered.HasValue)
            return;

        var cellValue = hovered.Value;
        DrawTooltip(context, position, new[]
        {
            $"{frame.Ticker}  Strike {DealerGexPresentation.FormatStrike(cellValue.StrikeUsd)}",
            $"GEX {FormatFullGex(cellValue.NetDealerGexUsd)}",
            $"Expiration {frame.Expiration:yyyy-MM-dd}",
            $"As-of {FormatDealerTime(frame.MinuteAtUtc)}"
        }, 270);
    }

    private void DrawDealerGexTooltip(
        RenderContext context,
        Rectangle dealerGexRect,
        decimal ratio)
    {
        var mouse = MouseLocationInfo;

        if (mouse == null || mouse.IsMouseLeave || mouse.IsMovingChartUsingMouse)
            return;

        var position = mouse.LastPosition;

        if (!dealerGexRect.Contains(position))
            return;

        var snapshot = Volatile.Read(ref _dealerGexSnapshot);

        if (snapshot.Ticker == null
            || !IsDealerGexFrameForTicker(snapshot.Frame, snapshot.Ticker)
            || snapshot.Frame is not { } frame)
        {
            return;
        }

        DealerGexNode? hovered = null;

        foreach (var node in frame.Nodes)
        {
            var rect = GetStrikeRectangle(
                dealerGexRect,
                frame.Ticker,
                node.StrikeUsd,
                ratio);

            if (rect?.Contains(position) == true)
            {
                hovered = node;
                break;
            }
        }

        if (!hovered.HasValue)
            return;

        var nodeValue = hovered.Value;
        DrawTooltip(context, position, new[]
        {
            $"{frame.Ticker}  Strike {DealerGexPresentation.FormatStrike(nodeValue.StrikeUsd)}",
            $"GEX {FormatFullGex(nodeValue.NetGexUsd)}",
            $"Type {nodeValue.NodeType}  Rank {nodeValue.Rank}",
            $"Strength {nodeValue.RelativeStrength.ToString("0.####", CultureInfo.InvariantCulture)}",
            $"Session {frame.SessionDateEt:yyyy-MM-dd}",
            $"As-of {FormatDealerTime(frame.SnapshotAtUtc)}"
        }, 300);
    }

    private void DrawTooltip(
        RenderContext context,
        Point position,
        IReadOnlyList<string> lines,
        int preferredWidth)
    {
        var region = ChartInfo!.PriceChartContainer.Region;
        var width = Math.Max(1, Math.Min(preferredWidth, region.Width - 8));
        var preferredHeight = 12 + lines.Count * 20;
        var height = Math.Max(1, Math.Min(preferredHeight, region.Height - 8));
        var left = Math.Clamp(
            position.X + 12,
            region.Left + 4,
            Math.Max(region.Left + 4, region.Right - width - 4));
        var top = Math.Clamp(
            position.Y + 12,
            region.Top + 4,
            Math.Max(region.Top + 4, region.Bottom - height - 4));
        var tooltip = new Rectangle(left, top, width, height);
        context.FillRectangle(DrawingColor.FromArgb(245, 18, 22, 28), tooltip);
        DrawBorder(context, tooltip, ConfiguredAxisBorderColor);

        for (var index = 0; index < lines.Count; index++)
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
        DrawingColor color,
        int thickness = 1)
    {
        for (var x = left; x < right; x += 6)
        {
            context.FillRectangle(
                color,
                new Rectangle(
                    x,
                    y,
                    Math.Min(3, right - x),
                    thickness));
        }
    }

    private string FormatDealerTime(DateTime utcTime)
    {
        var display = UiTimeZoneFormatter.ConvertFromUtc(
            DealerSamplingTime.FromBucketStartUtc(utcTime),
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

    private static string StateLabel(DealerGexState state)
        => state switch
        {
            DealerGexState.Live => "LIVE",
            DealerGexState.Closed => "CLOSED",
            DealerGexState.Frozen => "FROZEN",
            DealerGexState.AuthenticationFailed => "AUTH FAILED",
            DealerGexState.RateLimited => "RATE LIMITED",
            DealerGexState.Fetching => "FETCHING",
            DealerGexState.MissingApiKey => "NO KEY",
            DealerGexState.Disabled => "OFF",
            _ => "WAITING"
        };

    private readonly record struct StructureLine(
        string Label,
        decimal? ReferencePrice,
        DrawingColor Color,
        bool Dashed,
        int Y = 0);
}
